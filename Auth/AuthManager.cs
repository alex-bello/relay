using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

[JsonSerializable(typeof(ChatGptCredentials))]
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

  /// <summary>Clears only the "chatgpt" section — every other top-level key is left untouched.</summary>
  public async Task LogoutAsync(CancellationToken ct = default)
  {
    var root = await ReadRootAsync(ct);
    if (root.Remove(ChatGptKey))
      await WriteRootAsync(root, ct);
  }

  /// <summary>Decodes the `exp` claim of a JWT without verifying its signature — relay never holds the key to check it.</summary>
  internal static DateTimeOffset? DecodeExpiry(string jwt)
  {
    var parts = jwt.Split('.');
    if (parts.Length < 2)
      return null;

    try
    {
      using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
      return payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
          ? DateTimeOffset.FromUnixTimeSeconds(seconds)
          : null;
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }

  private static byte[] Base64UrlDecode(string value)
  {
    var padded = value.Replace('-', '+').Replace('_', '/');
    padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
    return Convert.FromBase64String(padded);
  }

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
