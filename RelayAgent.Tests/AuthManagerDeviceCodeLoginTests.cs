using System.Net;
using System.Text;
using RelayAgent.Auth;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises the headless `relay auth login --device-code` flow against a stubbed device-auth
/// and token endpoint (same fake-handler pattern as <see cref="AuthManagerLoginTests"/>) — no real
/// device, browser, or OpenAI account involved. The injected delay function stands in for
/// `Task.Delay` between polls: tests advance a manual clock instead of sleeping in real time, so a
/// simulated 15-minute timeout costs nothing.
/// </summary>
public class AuthManagerDeviceCodeLoginTests
{
  [Fact]
  public async Task LoginWithDeviceCodeAsync_displays_the_user_code_and_verification_url_before_polling()
  {
    var path = TempAuthPath();
    var handler = new FakeDeviceEndpoint(pendingPollsBeforeSuccess: 0, "id", "access", "refresh", "api-key");
    var manager = new AuthManager(new HttpClient(handler), new ManualTimeProvider(DateTimeOffset.UtcNow), path);

    try
    {
      string? shownCode = null;
      Uri? shownUrl = null;

      await manager.LoginWithDeviceCodeAsync(
          (code, url) => { shownCode = code; shownUrl = url; },
          NoOpDelay,
          CancellationToken.None);

      Assert.Equal("ABCD-1234", shownCode);
      Assert.Equal("https://auth.openai.com/deviceauth", shownUrl!.ToString());
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginWithDeviceCodeAsync_polls_until_success_then_exchanges_and_persists_credentials()
  {
    var path = TempAuthPath();
    var handler = new FakeDeviceEndpoint(pendingPollsBeforeSuccess: 2, "id-tok", "access-tok", "refresh-tok", "derived-key");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var manager = new AuthManager(new HttpClient(handler), clock, path);

    try
    {
      await manager.LoginWithDeviceCodeAsync((_, _) => { }, AdvancingDelay(clock), CancellationToken.None);

      Assert.Equal(3, handler.PollCount); // 2 pending + 1 success
      Assert.Equal("dev-1", handler.CapturedDeviceAuthId);
      Assert.Equal("ABCD-1234", handler.CapturedUserCode);
      Assert.Equal("verifier-xyz", handler.CapturedVerifier);
      Assert.Equal("https://auth.openai.com/deviceauth/callback", handler.CapturedRedirectUri);
      Assert.Equal("id-tok", handler.CapturedSubjectToken);

      var stored = await manager.ReadChatGptAsync();
      Assert.NotNull(stored);
      Assert.Equal("access-tok", stored!.AccessToken);
      Assert.Equal("refresh-tok", stored.RefreshToken);
      Assert.Equal("id-tok", stored.IdToken);
      Assert.Equal("derived-key", stored.ApiKey);
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginWithDeviceCodeAsync_gives_up_after_15_minutes_of_polling()
  {
    var path = TempAuthPath();
    var handler = new FakeDeviceEndpoint(pendingPollsBeforeSuccess: int.MaxValue, "id", "access", "refresh", "api-key");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var manager = new AuthManager(new HttpClient(handler), clock, path);

    try
    {
      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          manager.LoginWithDeviceCodeAsync((_, _) => { }, AdvancingDelay(clock), CancellationToken.None));

      Assert.Contains("15 minutes", ex.Message);
      Assert.False(File.Exists(path));
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  [Fact]
  public async Task LoginWithDeviceCodeAsync_surfaces_a_hard_error_from_the_poll_endpoint()
  {
    var path = TempAuthPath();
    var handler = new FakeDeviceEndpoint(pendingPollsBeforeSuccess: 0, "id", "access", "refresh", "api-key")
    {
      FailImmediately = true
    };
    var manager = new AuthManager(new HttpClient(handler), new ManualTimeProvider(DateTimeOffset.UtcNow), path);

    try
    {
      var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          manager.LoginWithDeviceCodeAsync((_, _) => { }, NoOpDelay, CancellationToken.None));

      Assert.Contains("access_denied", ex.Message);
      Assert.False(File.Exists(path));
    }
    finally
    {
      if (File.Exists(path)) File.Delete(path);
    }
  }

  private static Task NoOpDelay(TimeSpan interval, CancellationToken ct) => Task.CompletedTask;

  private static Func<TimeSpan, CancellationToken, Task> AdvancingDelay(ManualTimeProvider clock) =>
      (interval, _) =>
      {
        clock.Advance(interval);
        return Task.CompletedTask;
      };

  private static string TempAuthPath() =>
      Path.Combine(Path.GetTempPath(), $"relay-auth-device-login-test-{Guid.NewGuid():N}.json");

  private static Dictionary<string, string> ParseForm(string body)
  {
    var result = new Dictionary<string, string>();
    foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      var eq = pair.IndexOf('=');
      var key = eq >= 0 ? pair[..eq] : pair;
      var value = eq >= 0 ? pair[(eq + 1)..] : "";
      result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    return result;
  }

  private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
  {
    private DateTimeOffset _now = start;
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
  }

  /// <summary>Stubs the device-auth usercode/token endpoints plus `POST /oauth/token` for the exchange that follows a successful poll.</summary>
  private sealed class FakeDeviceEndpoint(int pendingPollsBeforeSuccess, string idToken, string accessToken, string refreshToken, string apiKey)
      : HttpMessageHandler
  {
    public bool FailImmediately { get; init; }
    public int PollCount { get; private set; }
    public string? CapturedDeviceAuthId { get; private set; }
    public string? CapturedUserCode { get; private set; }
    public string? CapturedVerifier { get; private set; }
    public string? CapturedRedirectUri { get; private set; }
    public string? CapturedSubjectToken { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      var path = request.RequestUri!.AbsolutePath;
      var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
      var fields = ParseForm(form);

      switch (path)
      {
        case "/api/accounts/deviceauth/usercode":
          return JsonResponse("""{"device_auth_id":"dev-1","user_code":"ABCD-1234","interval":1}""");

        case "/api/accounts/deviceauth/token":
          PollCount++;
          CapturedDeviceAuthId = fields.GetValueOrDefault("device_auth_id");
          CapturedUserCode = fields.GetValueOrDefault("user_code");

          if (FailImmediately)
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":"access_denied"}""", Encoding.UTF8, "application/json") };

          if (PollCount <= pendingPollsBeforeSuccess)
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":"authorization_pending"}""", Encoding.UTF8, "application/json") };

          return JsonResponse("""{"authorization_code":"device-auth-code","code_challenge":"chal","code_verifier":"verifier-xyz"}""");

        case "/oauth/token" when fields.GetValueOrDefault("grant_type") == "authorization_code":
          CapturedVerifier = fields["code_verifier"];
          CapturedRedirectUri = fields["redirect_uri"];
          return JsonResponse($$"""{"id_token":"{{idToken}}","access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}"}""");

        case "/oauth/token" when fields.GetValueOrDefault("grant_type") == "urn:ietf:params:oauth:grant-type:token-exchange":
          CapturedSubjectToken = fields["subject_token"];
          return JsonResponse($$"""{"access_token":"{{apiKey}}"}""");

        default:
          return new HttpResponseMessage(HttpStatusCode.NotFound);
      }
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
  }
}
