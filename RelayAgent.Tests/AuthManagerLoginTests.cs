using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayAgent.Auth;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises the browser-based `relay auth login` flow end to end against a real loopback socket
/// (same pattern as <see cref="LoopbackCallbackServerTests"/>) and a stubbed token endpoint — no
/// real browser or OpenAI account involved. The fake `openBrowser` delegate stands in for the
/// system browser: it captures the authorize URL and hands control back to the test, which then
/// drives the loopback callback itself, exactly as a real browser redirect would.
/// </summary>
public class AuthManagerLoginTests
{
  private static readonly HttpClient BrowserSimulator = new();

  [Fact]
  public async Task LoginAsync_completes_the_pkce_flow_and_persists_the_derived_credentials()
  {
    var path = TempAuthPath();
    var idToken = MakeJwt(AuthClaim("acct-42"));
    var accessToken = MakeJwt(("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));

    var handler = new FakeTokenEndpoint(idToken, accessToken, "refresh-1");
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);

      Assert.Equal("https", authorizeUrl.Scheme);
      Assert.Equal("auth.openai.com", authorizeUrl.Host);
      Assert.Equal("/oauth/authorize", authorizeUrl.AbsolutePath);
      Assert.Equal("code", query["response_type"]);
      Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", query["client_id"]);
      Assert.Equal("openid profile email offline_access api.connectors.read api.connectors.invoke", query["scope"]);
      Assert.Equal("S256", query["code_challenge_method"]);
      Assert.Equal("true", query["id_token_add_organizations"]);
      Assert.Equal("true", query["codex_cli_simplified_flow"]);
      Assert.False(string.IsNullOrEmpty(query["originator"]));
      Assert.False(string.IsNullOrEmpty(query["state"]));

      await CompleteCallbackAsync(query["redirect_uri"], query["state"]!);
      await loginTask;

      // PKCE correctness: the verifier the flow sent to the token endpoint must hash to the
      // challenge that was published in the authorize URL.
      var expectedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(handler.CapturedVerifier!)));
      Assert.Equal(query["code_challenge"], expectedChallenge);

      var stored = await manager.ReadChatGptAsync();
      Assert.NotNull(stored);
      Assert.Equal(accessToken, stored!.AccessToken);
      Assert.Equal("refresh-1", stored.RefreshToken);
      Assert.Equal(idToken, stored.IdToken);
      Assert.Equal("acct-42", stored.AccountId);
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginAsync_builds_an_authorize_url_whose_escaped_form_keeps_spaces_encoded()
  {
    // Regression guard for the browser-launch bug: the scopes are space-separated, so the
    // authorize URL only survives being handed to `open`/`xdg-open` if it is rendered via
    // AbsoluteUri (which keeps `%20`) rather than ToString() (which decodes it back to a
    // literal space, word-splitting the URL into bogus file-path arguments).
    var path = TempAuthPath();
    var handler = new FakeTokenEndpoint("id", "access", "refresh");
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);

      Assert.Contains("scope=openid%20profile%20email", authorizeUrl.AbsoluteUri);
      Assert.DoesNotContain(' ', authorizeUrl.AbsoluteUri);

      await CompleteCallbackAsync(query["redirect_uri"], query["state"]!);
      await loginTask;
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginAsync_reports_signed_in_status_after_a_successful_login()
  {
    var path = TempAuthPath();
    var idToken = MakeJwt(AuthClaim("acct-42"));
    var accessToken = MakeJwt(("exp", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()));

    var handler = new FakeTokenEndpoint(idToken, accessToken, "refresh-1");
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);
      await CompleteCallbackAsync(query["redirect_uri"], query["state"]!);
      await loginTask;

      var status = await manager.GetStatusAsync();

      Assert.True(status.SignedIn);
      Assert.NotNull(status.ExpiresIn);
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginAsync_rejects_a_callback_whose_state_does_not_match()
  {
    var path = TempAuthPath();
    var handler = new FakeTokenEndpoint("id", "access", "refresh");
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);

      await CompleteCallbackAsync(query["redirect_uri"], "a-completely-different-state");

      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loginTask);
      Assert.Contains("CSRF", ex.Message);
      Assert.False(File.Exists(path));
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginAsync_throws_when_the_flow_is_cancelled_before_completion()
  {
    var path = TempAuthPath();
    var handler = new FakeTokenEndpoint("id", "access", "refresh");
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);
      var cancelUri = new Uri(new Uri(query["redirect_uri"]!), "/cancel");

      using var response = await BrowserSimulator.GetAsync(cancelUri);

      await Assert.ThrowsAsync<InvalidOperationException>(() => loginTask);
      Assert.False(File.Exists(path));
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginAsync_surfaces_a_non_success_token_endpoint_response()
  {
    var path = TempAuthPath();
    var handler = new FakeTokenEndpoint("id", "access", "refresh") { FailCodeExchange = true };
    var manager = new AuthManager(new HttpClient(handler), TimeProvider.System, path);

    try
    {
      var (authorizeUrl, loginTask) = await RunLoginAndCaptureAuthorizeUrlAsync(manager);
      var query = ParseQuery(authorizeUrl.Query);
      await CompleteCallbackAsync(query["redirect_uri"], query["state"]!);

      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loginTask);
      Assert.Contains("400", ex.Message);
      Assert.False(File.Exists(path));
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  private static async Task<(Uri AuthorizeUrl, Task LoginTask)> RunLoginAndCaptureAuthorizeUrlAsync(AuthManager manager)
  {
    var authorizeUrlTcs = new TaskCompletionSource<Uri>();

    var loginTask = manager.LoginAsync((uri, ct) =>
    {
      authorizeUrlTcs.SetResult(uri);
      return Task.CompletedTask;
    }, CancellationToken.None);

    return (await authorizeUrlTcs.Task, loginTask);
  }

  private static async Task CompleteCallbackAsync(string? redirectUri, string state)
  {
    var uri = new Uri($"{redirectUri}?code=test-auth-code&state={Uri.EscapeDataString(state)}");
    using var response = await BrowserSimulator.GetAsync(uri);
  }

  private static string TempAuthPath() =>
      Path.Combine(Path.GetTempPath(), $"relay-auth-login-test-{Guid.NewGuid():N}.json");

  private static Dictionary<string, string?> ParseQuery(string query)
  {
    var result = new Dictionary<string, string?>();
    foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      var eq = pair.IndexOf('=');
      var key = eq >= 0 ? pair[..eq] : pair;
      var value = eq >= 0 ? pair[(eq + 1)..] : "";
      result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    return result;
  }

  private static string MakeJwt(params (string Claim, object Value)[] claims)
  {
    var header = Base64UrlEncode("""{"alg":"none"}"""u8.ToArray());
    var payloadObject = claims.ToDictionary(c => c.Claim, c => c.Value);
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObject)));
    return $"{header}.{payload}.sig";
  }

  /// <summary>The account id lives nested under the id_token's <c>https://api.openai.com/auth</c> claim, not at the top level.</summary>
  private static (string, object) AuthClaim(string accountId) =>
      ("https://api.openai.com/auth", new Dictionary<string, object> { ["chatgpt_account_id"] = accountId });

  private static string Base64UrlEncode(byte[] bytes) =>
      Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

  /// <summary>Stubs `POST /oauth/token` for the authorization-code grant the login flow uses; everything else 404s.</summary>
  private sealed class FakeTokenEndpoint(string idToken, string accessToken, string refreshToken)
      : HttpMessageHandler
  {
    public bool FailCodeExchange { get; init; }
    public string? CapturedVerifier { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      if (request.RequestUri?.AbsolutePath != "/oauth/token")
        return new HttpResponseMessage(HttpStatusCode.NotFound);

      var form = await request.Content!.ReadAsStringAsync(ct);
      var fields = ParseQuery(form);

      if (fields["grant_type"] == "authorization_code")
      {
        if (FailCodeExchange)
          return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid_grant") };

        CapturedVerifier = fields["code_verifier"];
        return JsonResponse($$"""
            {"id_token":"{{idToken}}","access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}"}
            """);
      }

      return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
  }
}
