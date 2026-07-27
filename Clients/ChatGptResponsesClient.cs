using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayAgent.OpenAI;

// ---------------------------------------------------------------------------
// The `chatgpt` backend does NOT speak the platform Chat Completions API. A
// ChatGPT sign-in produces an OAuth access token scoped to ChatGPT's own
// backend, not a platform API key — so requests go to
//   https://chatgpt.com/backend-api/codex/responses
// which is the Responses API (typed `input` items, SSE streaming), the same
// contract the Codex CLI uses. Two things the platform path never needs:
//   1. a `ChatGPT-Account-ID` header alongside the bearer token, and
//   2. server-sent events — the endpoint only streams; it has no JSON mode.
// We consume the whole stream and assemble one complete Message so the rest of
// relay stays non-streaming (see ILlmClient). This is deliberately a separate
// client from OpenAiClient rather than a branch inside it: the wire formats
// share almost nothing, so one class per format stays far more legible than a
// single method forking on backend halfway through.
// ---------------------------------------------------------------------------

/// <summary>Fresh per-request credentials for the ChatGPT backend: a bearer token (refreshed as needed by the auth manager) and the stable account id sent in the <c>ChatGPT-Account-ID</c> header.</summary>
public sealed record ChatGptRequestCredentials(string AccessToken, string AccountId);

// --- Responses API request wire types -------------------------------------
// One flat class per shape with null-omission does the job of a polymorphic
// `input` array without needing STJ polymorphism, which the source generator
// (this project is trimming-friendly) does not handle for our case.

internal sealed class ResponsesContentPart
{
  /// <summary>"input_text" for anything we send the model, "output_text" for prior assistant turns we replay.</summary>
  [JsonPropertyName("type")] public required string Type { get; set; }
  [JsonPropertyName("text")] public required string Text { get; set; }
}

internal sealed class ResponsesInputItem
{
  /// <summary>"message", "function_call", or "function_call_output" — the discriminator; unused fields below are omitted on the wire.</summary>
  [JsonPropertyName("type")] public required string Type { get; set; }
  [JsonPropertyName("role")] public string? Role { get; set; }
  [JsonPropertyName("content")] public List<ResponsesContentPart>? Content { get; set; }
  [JsonPropertyName("call_id")] public string? CallId { get; set; }
  [JsonPropertyName("name")] public string? Name { get; set; }
  /// <summary>A JSON *string* containing JSON, exactly as Chat Completions encodes tool-call arguments.</summary>
  [JsonPropertyName("arguments")] public string? Arguments { get; set; }
  [JsonPropertyName("output")] public string? Output { get; set; }
}

internal sealed class ResponsesTool
{
  [JsonPropertyName("type")] public string Type { get; set; } = "function";
  [JsonPropertyName("name")] public required string Name { get; set; }
  [JsonPropertyName("description")] public required string Description { get; set; }
  /// <summary>Tools live at the top level here, not nested under a "function" key the way Chat Completions nests them.</summary>
  [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
  [JsonPropertyName("strict")] public bool Strict { get; set; }
}

internal sealed class ResponsesRequest
{
  [JsonPropertyName("model")] public required string Model { get; set; }
  /// <summary>The system prompt: a top-level field here, not a message with role "system".</summary>
  [JsonPropertyName("instructions")] public string? Instructions { get; set; }
  [JsonPropertyName("input")] public required List<ResponsesInputItem> Input { get; set; }
  [JsonPropertyName("tools")] public List<ResponsesTool>? Tools { get; set; }
  [JsonPropertyName("tool_choice")] public string ToolChoice { get; set; } = "auto";
  [JsonPropertyName("parallel_tool_calls")] public bool ParallelToolCalls { get; set; }
  [JsonPropertyName("store")] public bool Store { get; set; }
  /// <summary>The endpoint only streams — there is no non-streaming mode — so this is always true.</summary>
  [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResponsesRequest))]
internal sealed partial class ChatGptJson : JsonSerializerContext;

// ---------------------------------------------------------------------------

public sealed class ChatGptResponsesClient : ILlmClient
{
  private readonly HttpClient _http;
  private readonly Func<CancellationToken, Task<ChatGptRequestCredentials>> _getCredentials;
  private readonly string _model;

  // A stable per-process session id, sent on every request like the Codex CLI does.
  private readonly string _sessionId = Guid.NewGuid().ToString();

  /// <summary>Parsed tool-call argument payloads, kept alive because the JsonElement handed out in a ToolUseBlock points into them.</summary>
  private readonly List<JsonDocument> _retained = [];

  public ChatGptResponsesClient(
      HttpClient http,
      Uri baseAddress,
      Func<CancellationToken, Task<ChatGptRequestCredentials>> getCredentials,
      string model)
  {
    _http = http;
    _getCredentials = getCredentials;
    _model = model;
    _http.BaseAddress ??= baseAddress;
  }

  public async Task<Message> CompleteAsync(
      IReadOnlyList<Message> messages,
      string systemPrompt,
      IReadOnlyList<ToolDefinition> tools,
      CancellationToken ct)
  {
    var input = new List<ResponsesInputItem>();
    foreach (var message in messages)
      input.AddRange(ToWire(message));

    var request = new ResponsesRequest
    {
      Model = _model,
      Instructions = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
      Input = input,
      Tools = tools.Count == 0 ? null : tools.Select(t => new ResponsesTool
      {
        Name = t.Name,
        Description = t.Description,
        Parameters = t.Schema
      }).ToList()
    };

    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "responses")
    {
      Content = JsonContent.Create(request, ChatGptJson.Default.ResponsesRequest)
    };

    var credentials = await _getCredentials(ct);
    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
    requestMessage.Headers.Add("ChatGPT-Account-ID", credentials.AccountId);
    requestMessage.Headers.Add("OpenAI-Beta", "responses=experimental");
    requestMessage.Headers.Add("originator", "codex_cli_rs");
    requestMessage.Headers.Add("session_id", _sessionId);
    requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    using var response = await _http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);

    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new HttpRequestException($"ChatGPT {(int)response.StatusCode}: {body}");
    }

    return await ReadStreamAsync(response, ct);
  }

  /// <summary>
  /// Drains the SSE stream into a single assistant Message. We rely on the fully-formed items in
  /// `response.output_item.done` rather than reassembling `*.delta` fragments — the `.done` event
  /// already carries the complete message text or function call — and stop at `response.completed`.
  /// </summary>
  private async Task<Message> ReadStreamAsync(HttpResponseMessage response, CancellationToken ct)
  {
    using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(stream);

    var blocks = new List<ContentBlock>();
    var completed = false;

    string? line;
    while ((line = await reader.ReadLineAsync(ct)) is not null)
    {
      if (!line.StartsWith("data:", StringComparison.Ordinal))
        continue;

      var data = line["data:".Length..].Trim();
      if (data.Length == 0 || data == "[DONE]")
        continue;

      using var doc = JsonDocument.Parse(data);
      var root = doc.RootElement;
      var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

      switch (type)
      {
        case "response.output_item.done" when root.TryGetProperty("item", out var item):
          AppendItem(item, blocks);
          break;

        case "response.completed":
          completed = true;
          break;

        // The endpoint reports a rejected/aborted generation in-band rather than via HTTP status.
        case "response.failed":
        case "response.incomplete":
          throw new HttpRequestException($"ChatGPT stream {type}: {data}");
      }

      if (completed)
        break;
    }

    if (!completed)
      throw new HttpRequestException("ChatGPT stream closed before response.completed.");

    return new Message(Role.Assistant, blocks);
  }

  /// <summary>Turns one completed output item — an assistant message or a function call — into domain content blocks.</summary>
  private void AppendItem(JsonElement item, List<ContentBlock> blocks)
  {
    var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

    switch (itemType)
    {
      case "message":
        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
          foreach (var part in content.EnumerateArray())
            // Assistant text arrives as "output_text" parts; anything else (refusals, etc.) is ignored.
            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text"
                && part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } s)
              blocks.Add(new TextBlock(s));
        break;

      case "function_call":
        var callId = item.GetProperty("call_id").GetString()!;
        var name = item.GetProperty("name").GetString()!;
        var arguments = item.TryGetProperty("arguments", out var a) ? a.GetString() : null;

        // Same defensive double-decode as the Chat Completions path: arguments is a JSON string.
        JsonDocument argsDoc;
        try
        {
          argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        }
        catch (JsonException)
        {
          argsDoc = JsonDocument.Parse("{}");
        }

        _retained.Add(argsDoc);
        blocks.Add(new ToolUseBlock(callId, name, argsDoc.RootElement));
        break;
    }
  }

  /// <summary>
  /// One neutral message fans out into Responses `input` items: assistant/user text becomes a
  /// `message` item, each tool call its own `function_call` item, and each tool result its own
  /// `function_call_output` item — the same one-to-many split the Chat Completions adapter does,
  /// just into a different item vocabulary.
  /// </summary>
  private static IEnumerable<ResponsesInputItem> ToWire(Message message)
  {
    if (message.Role == Role.Tool)
    {
      foreach (var result in message.Content.OfType<ToolResultBlock>())
        yield return new ResponsesInputItem
        {
          Type = "function_call_output",
          CallId = result.ToolUseId,
          Output = result.IsError ? $"ERROR: {result.Content}" : result.Content
        };
      yield break;
    }

    var isAssistant = message.Role == Role.Assistant;
    var text = string.Join("\n", message.Content.OfType<TextBlock>().Select(b => b.Text));
    if (!string.IsNullOrEmpty(text))
      yield return new ResponsesInputItem
      {
        Type = "message",
        Role = isAssistant ? "assistant" : "user",
        Content = [new ResponsesContentPart { Type = isAssistant ? "output_text" : "input_text", Text = text }]
      };

    foreach (var use in message.Content.OfType<ToolUseBlock>())
      yield return new ResponsesInputItem
      {
        Type = "function_call",
        CallId = use.Id,
        Name = use.Name,
        Arguments = use.Input.GetRawText()
      };
  }
}
