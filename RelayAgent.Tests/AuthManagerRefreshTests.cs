using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayAgent.Auth;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises the proactive access-token refresh added by ticket #17: the 5-minute-margin trigger,
/// the JSON (not form-urlencoded) refresh request, persisting the rotated refresh token while
/// leaving the derived API key untouched, the unified "not signed in" failure path, and adopting a
/// fresher token that a concurrent relay process already wrote.
/// </summary>
public class AuthManagerRefreshTests : IDisposable
{
  private readonly string _path = Path.Combine(Path.GetTempPath(), $"relay-auth-refresh-test-{Guid.NewGuid():N}.json");
  private readonly FixedTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));

  public void Dispose()
  {
    if (File.Exists(_path)) File.Delete(_path);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_returns_the_stored_token_unchanged_when_not_near_expiry()
  {
    var token = MakeJwt(_clock.GetUtcNow().AddMinutes(30));
    var handler = new FakeRefreshEndpoint();
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);
    await manager.WriteChatGptAsync(new ChatGptCredentials(token, "refresh-1", "id", "api-key", "acct-1"));

    var result = await manager.GetFreshAccessTokenAsync();

    Assert.Equal(token, result);
    Assert.Equal(0, handler.RefreshCallCount);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_refreshes_a_token_within_the_five_minute_margin()
  {
    var staleToken = MakeJwt(_clock.GetUtcNow().AddMinutes(4));
    var freshToken = MakeJwt(_clock.GetUtcNow().AddHours(1));
    var handler = new FakeRefreshEndpoint { AccessToken = freshToken, RefreshToken = "refresh-2" };
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);
    await manager.WriteChatGptAsync(new ChatGptCredentials(staleToken, "refresh-1", "id-old", "api-key", "acct-1"));

    var result = await manager.GetFreshAccessTokenAsync();

    Assert.Equal(freshToken, result);
    Assert.Equal(1, handler.RefreshCallCount);

    Assert.Equal("POST", handler.CapturedMethod);
    Assert.Equal("application/json", handler.CapturedContentType);
    Assert.Equal("refresh_token", handler.CapturedBody!["grant_type"]!.GetValue<string>());
    Assert.Equal("refresh-1", handler.CapturedBody["refresh_token"]!.GetValue<string>());
    Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", handler.CapturedBody["client_id"]!.GetValue<string>());

    var stored = await manager.ReadChatGptAsync();
    Assert.Equal(freshToken, stored!.AccessToken);
    Assert.Equal("refresh-2", stored.RefreshToken);
    Assert.Equal("api-key", stored.ApiKey);
    Assert.Equal("acct-1", stored.AccountId);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_throws_not_signed_in_when_no_credentials_are_stored()
  {
    var handler = new FakeRefreshEndpoint();
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetFreshAccessTokenAsync());

    Assert.Equal(AuthManager.NotSignedInMessage, ex.Message);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_surfaces_the_same_not_signed_in_message_when_refresh_fails()
  {
    var staleToken = MakeJwt(_clock.GetUtcNow().AddMinutes(1));
    var handler = new FakeRefreshEndpoint { FailRefresh = true };
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);
    await manager.WriteChatGptAsync(new ChatGptCredentials(staleToken, "refresh-1", "id", "api-key", "acct-1"));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetFreshAccessTokenAsync());

    Assert.Equal(AuthManager.NotSignedInMessage, ex.Message);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_adopts_a_fresher_token_a_concurrent_process_already_wrote_after_a_refresh_failure()
  {
    var staleToken = MakeJwt(_clock.GetUtcNow().AddMinutes(1));
    var concurrentToken = MakeJwt(_clock.GetUtcNow().AddHours(1));

    // Simulates a losing process in a refresh race: by the time its own refresh_token is rejected
    // (already consumed by the winner), the winner has already written its fresher credentials to
    // disk. The fake writes those credentials from inside the HTTP handler, at the moment our own
    // refresh call is in flight, then fails our call the way a reused refresh token would fail.
    var handler = new FakeRefreshEndpoint
    {
      FailRefresh = true,
      OnRefreshAttempt = () => WriteChatGptJson(_path, concurrentToken, "refresh-winner"),
    };
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);
    await manager.WriteChatGptAsync(new ChatGptCredentials(staleToken, "refresh-1", "id", "api-key", "acct-1"));

    var result = await manager.GetFreshAccessTokenAsync();

    Assert.Equal(concurrentToken, result);

    var stored = await manager.ReadChatGptAsync();
    Assert.Equal("refresh-winner", stored!.RefreshToken);
  }

  [Fact]
  public async Task GetFreshAccessTokenAsync_adopts_a_fresher_token_already_on_disk_without_calling_the_network()
  {
    var staleToken = MakeJwt(_clock.GetUtcNow().AddMinutes(1));
    var freshToken = MakeJwt(_clock.GetUtcNow().AddHours(1));
    WriteChatGptJson(_path, staleToken, "refresh-1");

    // A manager instance reads a stale, near-expiry token, but a concurrent process has already
    // refreshed and written a fresh one to disk before this call re-checks — it should be adopted
    // without ever calling the (network-failing) refresh endpoint.
    WriteChatGptJson(_path, freshToken, "refresh-winner");

    var handler = new FakeRefreshEndpoint { FailRefresh = true };
    var manager = new AuthManager(new HttpClient(handler), _clock, _path);

    var result = await manager.GetFreshAccessTokenAsync();

    Assert.Equal(freshToken, result);
    Assert.Equal(0, handler.RefreshCallCount);
  }

  /// <summary>Writes `auth.json` directly (bypassing <see cref="AuthManager"/>) to simulate a concurrent process's write landing on disk.</summary>
  private static void WriteChatGptJson(string path, string accessToken, string refreshToken)
  {
    var root = new JsonObject
    {
      ["chatgpt"] = new JsonObject
      {
        ["access_token"] = accessToken,
        ["refresh_token"] = refreshToken,
        ["id_token"] = "id",
        ["api_key"] = "api-key",
        ["account_id"] = "acct-1",
      },
    };
    File.WriteAllText(path, root.ToJsonString());
  }

  private static string MakeJwt(DateTimeOffset exp)
  {
    var header = Base64UrlEncode("""{"alg":"none"}"""u8.ToArray());
    var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { exp = exp.ToUnixTimeSeconds() })));
    return $"{header}.{payload}.sig";
  }

  private static string Base64UrlEncode(byte[] bytes) =>
      Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

  private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }

  /// <summary>Stubs the JSON refresh-grant request to `POST /oauth/token`; everything else 404s.</summary>
  private sealed class FakeRefreshEndpoint : HttpMessageHandler
  {
    public bool FailRefresh { get; init; }
    public string AccessToken { get; init; } = "unused";
    public string RefreshToken { get; init; } = "unused";
    public Action? OnRefreshAttempt { get; init; }

    public int RefreshCallCount { get; private set; }
    public string? CapturedMethod { get; private set; }
    public string? CapturedContentType { get; private set; }
    public JsonObject? CapturedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      if (request.RequestUri?.AbsolutePath != "/oauth/token")
        return new HttpResponseMessage(HttpStatusCode.NotFound);

      RefreshCallCount++;
      CapturedMethod = request.Method.Method;
      CapturedContentType = request.Content?.Headers.ContentType?.MediaType;
      var body = await request.Content!.ReadAsStringAsync(ct);
      CapturedBody = JsonNode.Parse(body)!.AsObject();

      OnRefreshAttempt?.Invoke();

      if (FailRefresh)
        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid_grant") };

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
            $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}"}""",
            Encoding.UTF8, "application/json"),
      };
    }
  }
}
