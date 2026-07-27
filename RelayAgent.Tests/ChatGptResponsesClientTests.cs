using System.Net;
using System.Text;
using System.Text.Json;
using RelayAgent;
using RelayAgent.OpenAI;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises the `chatgpt` backend's Responses-API adapter: it must target ChatGPT's
/// backend-api/codex/responses endpoint (not the platform API), attach the bearer token and the
/// `ChatGPT-Account-ID` header, translate the neutral domain model into typed Responses `input`
/// items, and assemble the server-sent-event stream back into a single assistant Message. The fake
/// endpoint stands in for chatgpt.com — no network, no real ChatGPT account.
/// </summary>
public class ChatGptResponsesClientTests
{
  private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

  [Fact]
  public async Task CompleteAsync_assembles_text_and_tool_calls_from_the_sse_stream()
  {
    var handler = new FakeResponsesEndpoint(Sse(
        """{"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hello there"}]}}""",
        """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}""",
        """{"type":"response.completed","response":{}}"""));

    var reply = await NewClient(handler).CompleteAsync([Message.User("hi")], "", [], default);

    var text = Assert.IsType<TextBlock>(reply.Content[0]);
    Assert.Equal("Hello there", text.Text);

    var call = Assert.IsType<ToolUseBlock>(reply.Content[1]);
    Assert.Equal("call_1", call.Id);
    Assert.Equal("read_file", call.Name);
    Assert.Equal("a.txt", call.Input.GetProperty("path").GetString());
  }

  [Fact]
  public async Task CompleteAsync_targets_the_responses_endpoint_with_auth_and_account_headers()
  {
    var handler = new FakeResponsesEndpoint(Sse("""{"type":"response.completed","response":{}}"""));

    await NewClient(handler, token: "tok-123", accountId: "acct-42")
        .CompleteAsync([Message.User("hi")], "be terse", [], default);

    Assert.Equal("/backend-api/codex/responses", handler.CapturedPath);
    Assert.Equal("Bearer", handler.CapturedAuthScheme);
    Assert.Equal("tok-123", handler.CapturedAuthParameter);
    Assert.Equal("acct-42", handler.CapturedAccountId);
    Assert.Equal("responses=experimental", handler.CapturedOpenAiBeta);
    Assert.Contains("text/event-stream", handler.CapturedAccept);
  }

  [Fact]
  public async Task CompleteAsync_serializes_the_request_as_a_streaming_responses_payload()
  {
    var handler = new FakeResponsesEndpoint(Sse("""{"type":"response.completed","response":{}}"""));
    var tool = new ToolDefinition("read_file", "Read a file", EmptySchema);

    await NewClient(handler).CompleteAsync([Message.User("hi")], "be terse", [tool], default);

    using var body = JsonDocument.Parse(handler.CapturedBody!);
    var root = body.RootElement;

    // System prompt is a top-level field, not a message.
    Assert.Equal("be terse", root.GetProperty("instructions").GetString());
    Assert.True(root.GetProperty("stream").GetBoolean());

    var firstInput = root.GetProperty("input")[0];
    Assert.Equal("message", firstInput.GetProperty("type").GetString());
    Assert.Equal("user", firstInput.GetProperty("role").GetString());
    Assert.Equal("input_text", firstInput.GetProperty("content")[0].GetProperty("type").GetString());
    Assert.Equal("hi", firstInput.GetProperty("content")[0].GetProperty("text").GetString());

    // Tools are flat (name at top level), unlike Chat Completions' nested "function" object.
    var firstTool = root.GetProperty("tools")[0];
    Assert.Equal("function", firstTool.GetProperty("type").GetString());
    Assert.Equal("read_file", firstTool.GetProperty("name").GetString());
  }

  [Fact]
  public async Task CompleteAsync_maps_assistant_tool_calls_and_tool_results_to_responses_items()
  {
    var handler = new FakeResponsesEndpoint(Sse("""{"type":"response.completed","response":{}}"""));

    var history = new List<Message>
    {
      Message.User("read it"),
      new(Role.Assistant, [new ToolUseBlock("call_1", "read_file", EmptySchema)]),
      new(Role.Tool, [new ToolResultBlock("call_1", "file contents")]),
    };

    await NewClient(handler).CompleteAsync(history, "", [], default);

    using var body = JsonDocument.Parse(handler.CapturedBody!);
    var input = body.RootElement.GetProperty("input");

    var functionCall = input[1];
    Assert.Equal("function_call", functionCall.GetProperty("type").GetString());
    Assert.Equal("call_1", functionCall.GetProperty("call_id").GetString());
    Assert.Equal("read_file", functionCall.GetProperty("name").GetString());

    var functionOutput = input[2];
    Assert.Equal("function_call_output", functionOutput.GetProperty("type").GetString());
    Assert.Equal("call_1", functionOutput.GetProperty("call_id").GetString());
    Assert.Equal("file contents", functionOutput.GetProperty("output").GetString());
  }

  [Fact]
  public async Task CompleteAsync_surfaces_an_in_band_stream_failure()
  {
    var handler = new FakeResponsesEndpoint(Sse(
        """{"type":"response.failed","response":{"error":{"message":"nope"}}}"""));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(
        () => NewClient(handler).CompleteAsync([Message.User("hi")], "", [], default));
    Assert.Contains("response.failed", ex.Message);
  }

  [Fact]
  public async Task CompleteAsync_surfaces_a_non_success_status_with_the_response_body()
  {
    var handler = new FakeResponsesEndpoint("unauthorized", HttpStatusCode.Unauthorized);

    var ex = await Assert.ThrowsAsync<HttpRequestException>(
        () => NewClient(handler).CompleteAsync([Message.User("hi")], "", [], default));
    Assert.Contains("401", ex.Message);
    Assert.Contains("unauthorized", ex.Message);
  }

  private static ChatGptResponsesClient NewClient(
      FakeResponsesEndpoint handler, string token = "tok", string accountId = "acct") =>
      new(new HttpClient(handler),
          new Uri("https://chatgpt.com/backend-api/codex/"),
          _ => Task.FromResult(new ChatGptRequestCredentials(token, accountId)),
          "gpt-5");

  private static string Sse(params string[] events) =>
      string.Concat(events.Select(e => $"data: {e}\n\n"));

  /// <summary>Stubs `POST /backend-api/codex/responses` with a canned SSE body; captures the request for assertions.</summary>
  private sealed class FakeResponsesEndpoint(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
  {
    public string? CapturedPath { get; private set; }
    public string? CapturedAuthScheme { get; private set; }
    public string? CapturedAuthParameter { get; private set; }
    public string? CapturedAccountId { get; private set; }
    public string? CapturedOpenAiBeta { get; private set; }
    public string? CapturedAccept { get; private set; }
    public string? CapturedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      CapturedPath = request.RequestUri?.AbsolutePath;
      CapturedAuthScheme = request.Headers.Authorization?.Scheme;
      CapturedAuthParameter = request.Headers.Authorization?.Parameter;
      CapturedAccountId = request.Headers.TryGetValues("ChatGPT-Account-ID", out var a) ? string.Join(",", a) : null;
      CapturedOpenAiBeta = request.Headers.TryGetValues("OpenAI-Beta", out var b) ? string.Join(",", b) : null;
      CapturedAccept = request.Headers.Accept.ToString();
      CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

      return new HttpResponseMessage(status)
      {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
      };
    }
  }
}
