using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayAgent.Auth;

namespace RelayAgent.Tests;

public class AuthManagerTests : IDisposable
{
  private readonly string _path = Path.Combine(Path.GetTempPath(), $"relay-auth-test-{Guid.NewGuid():N}.json");
  private readonly FixedTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));

  private AuthManager MakeManager() => new(new HttpClient(), _clock, _path);

  public void Dispose()
  {
    if (File.Exists(_path)) File.Delete(_path);
  }

  [Fact]
  public async Task ReadChatGptAsync_returns_null_when_no_file_exists()
  {
    var result = await MakeManager().ReadChatGptAsync();

    Assert.Null(result);
  }

  [Fact]
  public async Task WriteChatGptAsync_then_ReadChatGptAsync_round_trips()
  {
    var manager = MakeManager();
    var credentials = new ChatGptCredentials("access", "refresh", "id", "acct-1");

    await manager.WriteChatGptAsync(credentials);
    var result = await manager.ReadChatGptAsync();

    Assert.Equal(credentials, result);
  }

  [Fact]
  public async Task WriteChatGptAsync_creates_the_file_with_owner_only_permissions()
  {
    await MakeManager().WriteChatGptAsync(new ChatGptCredentials("a", "r", "i", null));

    Assert.True(File.Exists(_path));

    if (!OperatingSystem.IsWindows())
    {
      var mode = File.GetUnixFileMode(_path);
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
  }

  [Fact]
  public async Task WriteChatGptAsync_leaves_no_temp_file_behind()
  {
    await MakeManager().WriteChatGptAsync(new ChatGptCredentials("a", "r", "i", null));

    var directory = Path.GetDirectoryName(_path)!;
    var leftovers = Directory.GetFiles(directory, $"{Path.GetFileName(_path)}.*.tmp");
    Assert.Empty(leftovers);
  }

  [Fact]
  public async Task WriteChatGptAsync_preserves_other_top_level_keys_already_in_the_file()
  {
    await File.WriteAllTextAsync(_path, """{"other_setting":"keep-me"}""");

    await MakeManager().WriteChatGptAsync(new ChatGptCredentials("a", "r", "i", null));

    var root = JsonNode.Parse(await File.ReadAllTextAsync(_path))!.AsObject();
    Assert.Equal("keep-me", root["other_setting"]!.GetValue<string>());
    Assert.NotNull(root["chatgpt"]);
  }

  [Fact]
  public async Task LogoutAsync_clears_only_the_chatgpt_key()
  {
    await File.WriteAllTextAsync(_path, """{"chatgpt":{"access_token":"a"},"other_setting":"keep-me"}""");

    await MakeManager().LogoutAsync();

    var root = JsonNode.Parse(await File.ReadAllTextAsync(_path))!.AsObject();
    Assert.Null(root["chatgpt"]);
    Assert.Equal("keep-me", root["other_setting"]!.GetValue<string>());
  }

  [Fact]
  public async Task LogoutAsync_on_a_missing_file_does_not_create_one()
  {
    await MakeManager().LogoutAsync();

    Assert.False(File.Exists(_path));
  }

  [Fact]
  public async Task GetStatusAsync_reports_signed_in_with_remaining_time_for_an_unexpired_token()
  {
    var token = MakeJwt(_clock.GetUtcNow().AddMinutes(42));
    await MakeManager().WriteChatGptAsync(new ChatGptCredentials(token, "r", "i", null));

    var status = await MakeManager().GetStatusAsync();

    Assert.True(status.SignedIn);
    Assert.NotNull(status.ExpiresIn);
    Assert.InRange(status.ExpiresIn!.Value.TotalMinutes, 41.9, 42.1);
  }

  [Fact]
  public async Task GetStatusAsync_reports_not_signed_in_when_no_credentials_are_stored()
  {
    var status = await MakeManager().GetStatusAsync();

    Assert.False(status.SignedIn);
    Assert.Null(status.ExpiresIn);
  }

  [Fact]
  public async Task GetStatusAsync_reports_not_signed_in_for_an_expired_token()
  {
    var token = MakeJwt(_clock.GetUtcNow().AddMinutes(-5));
    await MakeManager().WriteChatGptAsync(new ChatGptCredentials(token, "r", "i", null));

    var status = await MakeManager().GetStatusAsync();

    Assert.False(status.SignedIn);
    Assert.Null(status.ExpiresIn);
  }

  [Theory]
  [InlineData("not-a-jwt")]
  [InlineData("only.two")]
  [InlineData("header.not-base64url!!.sig")]
  public async Task GetStatusAsync_reports_not_signed_in_for_a_malformed_access_token(string token)
  {
    await MakeManager().WriteChatGptAsync(new ChatGptCredentials(token, "r", "i", null));

    var status = await MakeManager().GetStatusAsync();

    Assert.False(status.SignedIn);
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
}
