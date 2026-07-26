using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RelayAgent;
using RelayAgent.OpenAI;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises ticket #18's credential-source seam: <see cref="OpenAiClient"/> must ask its
/// <see cref="ICredentialSource"/> for a Bearer token before every request rather than fixing one
/// at construction, <see cref="StaticCredentialSource"/> must reproduce the previous
/// constructor-header behavior unchanged (including sending no header at all for a null/empty
/// key), and <see cref="DelegateCredentialSource"/> must forward to an arbitrary async token
/// getter (standing in for the chatgpt backend's <c>AuthManager.GetFreshAccessTokenAsync</c>).
/// </summary>
public class OpenAiClientCredentialSourceTests
{
  [Fact]
  public async Task StaticCredentialSource_sends_the_configured_key_as_a_bearer_header()
  {
    var handler = new FakeCompletionsEndpoint();
    var client = new OpenAiClient(new HttpClient(handler), new Uri("http://unused/"), new StaticCredentialSource("static-key"), "model");

    await client.CompleteAsync([Message.User("hi")], "", [], default);

    Assert.NotNull(handler.CapturedAuthorization);
    Assert.Equal("Bearer", handler.CapturedAuthorization!.Scheme);
    Assert.Equal("static-key", handler.CapturedAuthorization.Parameter);
  }

  [Fact]
  public async Task StaticCredentialSource_sends_no_authorization_header_when_the_key_is_null()
  {
    var handler = new FakeCompletionsEndpoint();
    var client = new OpenAiClient(new HttpClient(handler), new Uri("http://unused/"), new StaticCredentialSource(null), "model");

    await client.CompleteAsync([Message.User("hi")], "", [], default);

    Assert.Null(handler.CapturedAuthorization);
  }

  [Fact]
  public async Task DelegateCredentialSource_forwards_to_the_wrapped_token_getter_on_every_call()
  {
    var tokens = new Queue<string>(["token-1", "token-2"]);
    var handler = new FakeCompletionsEndpoint();
    var client = new OpenAiClient(
        new HttpClient(handler), new Uri("http://unused/"), new DelegateCredentialSource(_ => Task.FromResult(tokens.Dequeue())), "model");

    await client.CompleteAsync([Message.User("hi")], "", [], default);
    Assert.Equal("token-1", handler.CapturedAuthorization!.Parameter);

    await client.CompleteAsync([Message.User("hi")], "", [], default);
    Assert.Equal("token-2", handler.CapturedAuthorization!.Parameter);
  }

  /// <summary>Stubs `POST v1/chat/completions` with a minimal valid response; everything else 404s.</summary>
  private sealed class FakeCompletionsEndpoint : HttpMessageHandler
  {
    public AuthenticationHeaderValue? CapturedAuthorization { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      if (request.RequestUri?.AbsolutePath != "/v1/chat/completions")
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

      CapturedAuthorization = request.Headers.Authorization;

      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(
            """{"choices":[{"message":{"role":"assistant","content":"hello"}}]}""",
            Encoding.UTF8, "application/json"),
      });
    }
  }
}
