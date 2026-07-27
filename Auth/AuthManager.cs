using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RelayAgent.Auth;

// ---------------------------------------------------------------------------
// auth.json's schema keys credentials by backend name so future OAuth backends
// (and future non-auth settings, tracked separately) can share the file
// without colliding. This ticket only populates the "chatgpt" section.
// ---------------------------------------------------------------------------

public sealed record ChatGptCredentials(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("api_key")] string? ApiKey,
    [property: JsonPropertyName("account_id")] string? AccountId);

public sealed record AuthStatus(bool SignedIn, TimeSpan? ExpiresIn);

/// <summary>
/// Raised when there is no usable ChatGPT session — no stored credentials, or a refresh token the
/// server no longer accepts. Subclasses <see cref="InvalidOperationException"/> so callers that
/// catch that (and assert on <see cref="AuthManager.NotSignedInMessage"/>) keep working, while the
/// REPL can catch this specific type to offer an inline re-login instead of only printing the error.
/// </summary>
public sealed class NotSignedInException() : InvalidOperationException(AuthManager.NotSignedInMessage);

/// <summary>Response shape shared by the authorization-code exchange and the refresh grant (ticket #17); every field is optional on refresh, required on first login.</summary>
internal sealed class TokenResponse
{
  [JsonPropertyName("id_token")] public string? IdToken { get; set; }
  [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
  [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
}

/// <summary>Response shape of the RFC 8693 token-exchange grant that derives relay's platform-style API key from the ID token.</summary>
internal sealed class ApiKeyResponse
{
  [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
}

/// <summary>Body of the refresh grant (ticket #17). Sent as a JSON object, unlike every other grant this file uses, which are form-urlencoded.</summary>
internal sealed class RefreshRequest
{
  [JsonPropertyName("client_id")] public required string ClientId { get; set; }
  [JsonPropertyName("grant_type")] public required string GrantType { get; set; }
  [JsonPropertyName("refresh_token")] public required string RefreshToken { get; set; }
}

/// <summary>Response shape of `POST /api/accounts/deviceauth/usercode`, the first step of the headless device-code flow.</summary>
internal sealed class DeviceUserCodeResponse
{
  [JsonPropertyName("device_auth_id")] public string? DeviceAuthId { get; set; }
  [JsonPropertyName("user_code")] public string? UserCode { get; set; }
  [JsonPropertyName("interval")] public int? Interval { get; set; }
}

/// <summary>
/// Response shape of each `POST /api/accounts/deviceauth/token` poll. While the user hasn't yet
/// entered the code, the server answers with `error: "authorization_pending"`; on success it
/// answers with the fields needed to run the same code-exchange call the browser flow uses.
/// </summary>
internal sealed class DeviceTokenPollResponse
{
  [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; set; }
  [JsonPropertyName("code_verifier")] public string? CodeVerifier { get; set; }
  [JsonPropertyName("error")] public string? Error { get; set; }
}

[JsonSerializable(typeof(ChatGptCredentials))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(ApiKeyResponse))]
[JsonSerializable(typeof(DeviceUserCodeResponse))]
[JsonSerializable(typeof(DeviceTokenPollResponse))]
[JsonSerializable(typeof(RefreshRequest))]
internal sealed partial class AuthJson : JsonSerializerContext;

/// <summary>
/// Owns `~/.relay/auth.json`: atomic, owner-only-permissioned read/write of the
/// "chatgpt" section, plus JWT `exp`-claim decoding. Login/refresh (later
/// tickets) extend this same component, so its dependencies — HTTP transport,
/// clock, and the auth.json path — are all injected here even though this
/// ticket's operations (status, logout) never touch the network.
/// </summary>
public sealed class AuthManager
{
  private const string ChatGptKey = "chatgpt";

  /// <summary>The one "not authenticated" message: no stored session and a dead refresh token both surface this — never distinct messaging per cause.</summary>
  public const string NotSignedInMessage = "Not signed in — run 'relay auth login'";

  // Matches Codex's own refresh margin: a token is refreshed ahead of expiry rather than waited out to a 401.
  private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

  // Fixed issuer/client, matching Codex CLI's own ("Sign in with ChatGPT") flow exactly — this
  // is a public PKCE client registered against auth.openai.com, not relay's own credential.
  private const string Issuer = "https://auth.openai.com";
  private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
  private const string Scopes = "openid profile email offline_access api.connectors.read api.connectors.invoke";
  private const string Originator = "relay_cli";

  // Headless fallback (ticket #16) for hosts with no local browser. Same issuer/client as the
  // browser flow; the server — not relay — owns PKCE for this path, handing back a code_verifier
  // alongside the authorization_code once the user enters the code.
  private static readonly TimeSpan DeviceCodeMaxWait = TimeSpan.FromMinutes(15);

  private readonly HttpClient _http;
  private readonly TimeProvider _clock;
  private readonly string _path;

  public AuthManager(HttpClient http, TimeProvider clock, string path)
  {
    _http = http;
    _clock = clock;
    _path = path;
  }

  public static string DefaultPath() =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".relay", "auth.json");

  /// <summary>
  /// Runs the full browser-based "Sign in with ChatGPT" flow end to end: starts the loopback
  /// callback server, opens the system browser to a freshly-built PKCE authorize URL, waits for
  /// the redirect, exchanges the code for tokens, derives relay's platform API key via an RFC
  /// 8693 token-exchange grant, and persists the result. Throws on cancellation, a CSRF (state
  /// mismatch) failure, or any non-success response from the token endpoint.
  /// </summary>
  public Task LoginAsync(CancellationToken ct = default) => LoginAsync(OpenBrowserAsync, ct);

  /// <summary>Test seam: production always opens a real system browser; tests substitute a fake that drives the loopback callback itself instead.</summary>
  internal async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
  {
    using var server = await LoopbackCallbackServer.StartAsync(_http, ct);

    var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
    var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    await openBrowser(BuildAuthorizeUrl(server.RedirectUri, challenge, state), ct);

    var callback = await server.WaitForCallbackAsync(ct)
        ?? throw new InvalidOperationException("Sign-in was cancelled before it completed.");

    if (!callback.TryGetValue("state", out var returnedState) || returnedState != state)
      throw new InvalidOperationException("OAuth callback failed a CSRF check: the returned state did not match.");

    if (!callback.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
    {
      var reason = callback.TryGetValue("error_description", out var description) ? description
          : callback.TryGetValue("error", out var error) ? error
          : "no authorization code was returned";
      throw new InvalidOperationException($"ChatGPT sign-in failed: {reason}.");
    }

    var tokens = await ExchangeCodeAsync(code, verifier, server.RedirectUri, ct);
    var apiKey = await DeriveApiKeyAsync(tokens.IdToken!, ct);

    await WriteChatGptAsync(
        new ChatGptCredentials(tokens.AccessToken, tokens.RefreshToken, tokens.IdToken, apiKey, DecodeClaim(tokens.IdToken!, "chatgpt_account_id")),
        ct);
  }

  /// <summary>
  /// Runs the headless "Sign in with ChatGPT" flow for hosts with no local browser: requests a
  /// user code, hands it to <paramref name="onCodeReady"/> to display, polls for the user to enter
  /// it elsewhere, then exchanges and persists the result exactly like <see cref="LoginAsync(CancellationToken)"/>.
  /// Gives up with an explicit error after 15 minutes rather than polling indefinitely.
  /// </summary>
  public Task LoginWithDeviceCodeAsync(Action<string, Uri> onCodeReady, CancellationToken ct = default) =>
      LoginWithDeviceCodeAsync(onCodeReady, static (interval, token) => Task.Delay(interval, token), ct);

  /// <summary>Test seam: production always waits out the real poll interval via <see cref="Task.Delay(TimeSpan,CancellationToken)"/>; tests substitute a fake that advances a fake clock instead.</summary>
  internal async Task LoginWithDeviceCodeAsync(
      Action<string, Uri> onCodeReady, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
  {
    var userCode = await RequestDeviceUserCodeAsync(ct);
    onCodeReady(userCode.UserCode!, new Uri($"{Issuer}/deviceauth"));

    var interval = TimeSpan.FromSeconds(userCode.Interval!.Value);
    var deadline = _clock.GetUtcNow() + DeviceCodeMaxWait;

    DeviceTokenPollResponse poll;
    while (true)
    {
      if (_clock.GetUtcNow() >= deadline)
        throw new InvalidOperationException($"ChatGPT device-code sign-in timed out after {DeviceCodeMaxWait.TotalMinutes:0} minutes.");

      poll = await PollDeviceTokenAsync(userCode.DeviceAuthId!, userCode.UserCode!, ct);
      if (!string.IsNullOrEmpty(poll.AuthorizationCode))
        break;

      await delay(interval, ct);
    }

    if (string.IsNullOrEmpty(poll.CodeVerifier))
      throw new InvalidOperationException("ChatGPT device-code token endpoint returned an authorization code without a code verifier.");

    var tokens = await ExchangeCodeAsync(poll.AuthorizationCode!, poll.CodeVerifier, new Uri($"{Issuer}/deviceauth/callback"), ct);
    var apiKey = await DeriveApiKeyAsync(tokens.IdToken!, ct);

    await WriteChatGptAsync(
        new ChatGptCredentials(tokens.AccessToken, tokens.RefreshToken, tokens.IdToken, apiKey, DecodeClaim(tokens.IdToken!, "chatgpt_account_id")),
        ct);
  }

  private async Task<DeviceUserCodeResponse> RequestDeviceUserCodeAsync(CancellationToken ct)
  {
    var result = await PostFormAsync(
        $"{Issuer}/api/accounts/deviceauth/usercode",
        new Dictionary<string, string> { ["client_id"] = ClientId },
        AuthJson.Default.DeviceUserCodeResponse,
        ct);

    if (string.IsNullOrEmpty(result.DeviceAuthId) || string.IsNullOrEmpty(result.UserCode) || result.Interval is not > 0)
      throw new InvalidOperationException("ChatGPT device-code endpoint response was missing a required field.");

    return result;
  }

  private async Task<DeviceTokenPollResponse> PollDeviceTokenAsync(string deviceAuthId, string userCode, CancellationToken ct)
  {
    using var response = await _http.PostAsync(
        $"{Issuer}/api/accounts/deviceauth/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
          ["device_auth_id"] = deviceAuthId,
          ["user_code"] = userCode,
        }),
        ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    var result = JsonSerializer.Deserialize(body, AuthJson.Default.DeviceTokenPollResponse)
        ?? throw new InvalidOperationException("ChatGPT device-code token endpoint returned an empty response.");

    if (response.IsSuccessStatusCode || result.Error == "authorization_pending")
      return result;

    throw new InvalidOperationException($"ChatGPT device-code sign-in failed: {result.Error ?? $"token endpoint returned {(int)response.StatusCode}"}.");
  }

  private static Uri BuildAuthorizeUrl(Uri redirectUri, string challenge, string state)
  {
    var query = new Dictionary<string, string>
    {
      ["response_type"] = "code",
      ["client_id"] = ClientId,
      ["redirect_uri"] = redirectUri.ToString(),
      ["scope"] = Scopes,
      ["code_challenge"] = challenge,
      ["code_challenge_method"] = "S256",
      ["id_token_add_organizations"] = "true",
      ["codex_cli_simplified_flow"] = "true",
      ["originator"] = Originator,
      ["state"] = state,
    };

    var pairs = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
    return new Uri($"{Issuer}/oauth/authorize?{string.Join('&', pairs)}");
  }

  private async Task<TokenResponse> ExchangeCodeAsync(string code, string verifier, Uri redirectUri, CancellationToken ct)
  {
    var tokens = await PostTokenEndpointAsync<TokenResponse>(
        new Dictionary<string, string>
        {
          ["grant_type"] = "authorization_code",
          ["code"] = code,
          ["redirect_uri"] = redirectUri.ToString(),
          ["client_id"] = ClientId,
          ["code_verifier"] = verifier,
        },
        AuthJson.Default.TokenResponse,
        ct);

    if (string.IsNullOrEmpty(tokens.IdToken) || string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken))
      throw new InvalidOperationException("ChatGPT token exchange response was missing a required field.");

    return tokens;
  }

  private async Task<string> DeriveApiKeyAsync(string idToken, CancellationToken ct)
  {
    var result = await PostTokenEndpointAsync<ApiKeyResponse>(
        new Dictionary<string, string>
        {
          ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
          ["client_id"] = ClientId,
          ["requested_token"] = "openai-api-key",
          ["subject_token"] = idToken,
          ["subject_token_type"] = "urn:ietf:params:oauth:token-type:id_token",
        },
        AuthJson.Default.ApiKeyResponse,
        ct);

    return string.IsNullOrEmpty(result.AccessToken)
        ? throw new InvalidOperationException("ChatGPT API key derivation response was missing 'access_token'.")
        : result.AccessToken;
  }

  private Task<T> PostTokenEndpointAsync<T>(Dictionary<string, string> form, JsonTypeInfo<T> typeInfo, CancellationToken ct) =>
      PostFormAsync($"{Issuer}/oauth/token", form, typeInfo, ct);

  /// <summary>Shared shape for the two auth endpoints (token exchange, device-code usercode) that always succeed-or-throw. The device-code poll endpoint has its own handling — a specific error there means "keep waiting," not "fail" — so it posts and parses independently instead of going through this.</summary>
  private async Task<T> PostFormAsync<T>(string url, Dictionary<string, string> form, JsonTypeInfo<T> typeInfo, CancellationToken ct) =>
      await ParseOrThrowAsync(await _http.PostAsync(url, new FormUrlEncodedContent(form), ct), typeInfo, ct);

  /// <summary>Shared succeed-or-throw response handling for every token-endpoint call in this file, whether the request body was form-urlencoded or (the refresh grant's) JSON.</summary>
  private static async Task<T> ParseOrThrowAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
  {
    using var _ = response;
    var body = await response.Content.ReadAsStringAsync(ct);

    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException($"ChatGPT endpoint returned {(int)response.StatusCode}: {body}");

    return JsonSerializer.Deserialize(body, typeInfo)
        ?? throw new InvalidOperationException("ChatGPT endpoint returned an empty response.");
  }

  private static Task OpenBrowserAsync(Uri uri, CancellationToken ct)
  {
    // AbsoluteUri (not ToString()) preserves percent-encoding; ToString() decodes %20 back to
    // literal spaces, which then word-split the URL into multiple argv entries. ArgumentList
    // passes the URL as a single argument regardless, so the launcher never re-splits it.
    var url = uri.AbsoluteUri;
    var startInfo = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
        : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
    if (!OperatingSystem.IsWindows())
      startInfo.ArgumentList.Add(url);

    Process.Start(startInfo);
    return Task.CompletedTask;
  }

  public async Task<ChatGptCredentials?> ReadChatGptAsync(CancellationToken ct = default)
  {
    var root = await ReadRootAsync(ct);
    return root[ChatGptKey]?.Deserialize(AuthJson.Default.ChatGptCredentials);
  }

  public async Task WriteChatGptAsync(ChatGptCredentials credentials, CancellationToken ct = default)
  {
    var root = await ReadRootAsync(ct);
    root[ChatGptKey] = JsonSerializer.SerializeToNode(credentials, AuthJson.Default.ChatGptCredentials);
    await WriteRootAsync(root, ct);
  }

  public async Task<AuthStatus> GetStatusAsync(CancellationToken ct = default)
  {
    var accessToken = (await ReadChatGptAsync(ct))?.AccessToken;
    if (string.IsNullOrEmpty(accessToken))
      return new AuthStatus(SignedIn: false, ExpiresIn: null);

    var expiry = DecodeExpiry(accessToken);
    var remaining = expiry - _clock.GetUtcNow();
    if (expiry is null || remaining <= TimeSpan.Zero)
      return new AuthStatus(SignedIn: false, ExpiresIn: null);

    return new AuthStatus(SignedIn: true, ExpiresIn: remaining);
  }

  /// <summary>
  /// Returns an access token guaranteed valid for at least <see cref="RefreshMargin"/>, refreshing
  /// the stored credentials first if the current one is within that margin of expiry (or already
  /// expired) — a proactive check meant to run before every completion call, not a reaction to a
  /// 401. Two processes can race this: before spending a network call, and again if that call
  /// fails, this re-reads auth.json and silently adopts whatever a concurrent process already
  /// wrote there rather than overwriting it or treating a benign race loss as a dead session.
  /// Throws with <see cref="NotSignedInMessage"/> — the same message the no-token case
  /// surfaces — when there's no stored session or the refresh token itself no longer works.
  /// </summary>
  public async Task<string> GetFreshAccessTokenAsync(CancellationToken ct = default)
  {
    var stored = await ReadChatGptAsync(ct);
    if (string.IsNullOrEmpty(stored?.AccessToken) || string.IsNullOrEmpty(stored.RefreshToken))
      throw new NotSignedInException();

    if (!IsNearExpiry(stored.AccessToken))
      return stored.AccessToken;

    // Another process may have already refreshed since the read above landed.
    var current = await TryReadFreshAsync(ct) ?? stored;
    if (!IsNearExpiry(current.AccessToken!))
      return current.AccessToken!;

    try
    {
      var tokens = await RefreshTokenAsync(current.RefreshToken!, ct);
      var updated = current with
      {
        AccessToken = tokens.AccessToken ?? current.AccessToken,
        RefreshToken = tokens.RefreshToken ?? current.RefreshToken,
        IdToken = tokens.IdToken ?? current.IdToken,
      };

      await WriteChatGptAsync(updated, ct);
      return updated.AccessToken!;
    }
    catch (InvalidOperationException)
    {
      // Only a rejected grant (4xx — RefreshTokenAsync lets a 5xx propagate as HttpRequestException
      // instead, uncaught here) lands in this block. Most likely cause: another process already
      // consumed this (single-use, rotating) refresh token and won the race. Adopt its result if it
      // landed; only report "not signed in" if the refresh token was genuinely dead.
      var afterFailure = await TryReadFreshAsync(ct);
      return afterFailure?.AccessToken ?? throw new NotSignedInException();
    }
  }

  /// <summary>Reads the stored credentials and returns them only if present with an access token that isn't near expiry; null otherwise.</summary>
  private async Task<ChatGptCredentials?> TryReadFreshAsync(CancellationToken ct)
  {
    var credentials = await ReadChatGptAsync(ct);
    return !string.IsNullOrEmpty(credentials?.AccessToken) && !IsNearExpiry(credentials.AccessToken)
        ? credentials
        : null;
  }

  private bool IsNearExpiry(string accessToken)
  {
    var expiry = DecodeExpiry(accessToken);
    return expiry is null || expiry - _clock.GetUtcNow() <= RefreshMargin;
  }

  private async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct)
  {
    var request = new RefreshRequest { ClientId = ClientId, GrantType = "refresh_token", RefreshToken = refreshToken };
    var response = await _http.PostAsJsonAsync($"{Issuer}/oauth/token", request, AuthJson.Default.RefreshRequest, ct);

    // A 5xx here means the token endpoint itself is unwell, not that the refresh token was
    // rejected — that must not be mistaken by the caller's catch below for a dead session.
    if ((int)response.StatusCode >= 500)
    {
      using var _ = response;
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new HttpRequestException($"ChatGPT refresh endpoint returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }

    return await ParseOrThrowAsync(response, AuthJson.Default.TokenResponse, ct);
  }

  /// <summary>Clears only the "chatgpt" section — every other top-level key is left untouched.</summary>
  public async Task LogoutAsync(CancellationToken ct = default)
  {
    var root = await ReadRootAsync(ct);
    if (root.Remove(ChatGptKey))
      await WriteRootAsync(root, ct);
  }

  /// <summary>Decodes the `exp` claim of a JWT without verifying its signature — relay never holds the key to check it.</summary>
  internal static DateTimeOffset? DecodeExpiry(string jwt) =>
      WithDecodedPayload(jwt, payload => payload.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
          ? DateTimeOffset.FromUnixTimeSeconds(seconds)
          : (DateTimeOffset?)null);

  /// <summary>Decodes an arbitrary string claim from a JWT's payload without verifying its signature. Returns null for a missing claim or an unparseable token.</summary>
  private static string? DecodeClaim(string jwt, string claim) =>
      WithDecodedPayload(jwt, payload => payload.TryGetProperty(claim, out var value) ? value.GetString() : null);

  private static T? WithDecodedPayload<T>(string jwt, Func<JsonElement, T?> select)
  {
    var parts = jwt.Split('.');
    if (parts.Length < 2)
      return default;

    try
    {
      using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
      return select(payload.RootElement);
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return default;
    }
  }

  private static byte[] Base64UrlDecode(string value)
  {
    var padded = value.Replace('-', '+').Replace('_', '/');
    padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
    return Convert.FromBase64String(padded);
  }

  private static string Base64UrlEncode(byte[] bytes) =>
      Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

  private async Task<JsonObject> ReadRootAsync(CancellationToken ct)
  {
    if (!File.Exists(_path))
      return [];

    await using var stream = File.OpenRead(_path);
    return await JsonNode.ParseAsync(stream, cancellationToken: ct) as JsonObject ?? [];
  }

  private async Task WriteRootAsync(JsonObject root, CancellationToken ct)
  {
    var directory = Path.GetDirectoryName(_path);
    if (!string.IsNullOrEmpty(directory))
      Directory.CreateDirectory(directory);

    var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";

    // Owner-only mode is requested at creation, not applied after the fact —
    // the file holds plaintext tokens, so it must never be briefly readable
    // under the process's default umask while content is being written.
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (!OperatingSystem.IsWindows())
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    await using (var stream = new FileStream(tempPath, options))
    await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
      root.WriteTo(writer);

    File.Move(tempPath, _path, overwrite: true);
  }
}
