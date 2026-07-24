using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayAgent.OpenAI;

// ---------------------------------------------------------------------------
// Same job as the Anthropic adapter, notably different shape. Read the two
// ToWire methods side by side — the diff between them is the whole reason the
// neutral domain model exists.
// ---------------------------------------------------------------------------

internal sealed class WireFunctionCall
{
  [JsonPropertyName("name")] public required string Name { get; set; }
  /// <summary>A JSON *string* containing JSON. Not an object. Yes, really.</summary>
  [JsonPropertyName("arguments")] public required string Arguments { get; set; }
}

internal sealed class WireToolCall
{
  [JsonPropertyName("id")] public required string Id { get; set; }
  [JsonPropertyName("type")] public string Type { get; set; } = "function";
  [JsonPropertyName("function")] public required WireFunctionCall Function { get; set; }
}

internal sealed class WireMessage
{
  [JsonPropertyName("role")] public required string Role { get; set; }
  [JsonPropertyName("content")] public string? Content { get; set; }
  [JsonPropertyName("tool_calls")] public List<WireToolCall>? ToolCalls { get; set; }
  [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
}

internal sealed class WireFunctionDef
{
  [JsonPropertyName("name")] public required string Name { get; set; }
  [JsonPropertyName("description")] public required string Description { get; set; }
  [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
}

internal sealed class WireTool
{
  [JsonPropertyName("type")] public string Type { get; set; } = "function";
  [JsonPropertyName("function")] public required WireFunctionDef Function { get; set; }
}

internal sealed class WireRequest
{
  [JsonPropertyName("model")] public required string Model { get; set; }
  [JsonPropertyName("messages")] public required List<WireMessage> Messages { get; set; }
  [JsonPropertyName("tools")] public List<WireTool>? Tools { get; set; }
  [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 8192;
}

internal sealed class WireChoice
{
  [JsonPropertyName("message")] public WireMessage? Message { get; set; }
  [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal sealed class WireResponse
{
  [JsonPropertyName("choices")] public List<WireChoice> Choices { get; set; } = [];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(WireResponse))]
internal sealed partial class OpenAiJson : JsonSerializerContext;

// ---------------------------------------------------------------------------

public sealed class OpenAiClient : ILlmClient
{
  private readonly HttpClient _http;
  private readonly string _model;

  /// <summary>Parsed argument payloads, kept alive because JsonElement points into them.</summary>
  private readonly List<JsonDocument> _retained = [];

  public OpenAiClient(HttpClient http, Uri baseAddress, string? apiKey, string model)
  {
    _http = http;
    _model = model;
    _http.BaseAddress ??= baseAddress;
    if (!string.IsNullOrEmpty(apiKey))
      _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
  }

  public async Task<Message> CompleteAsync(
      IReadOnlyList<Message> messages,
      string systemPrompt,
      IReadOnlyList<ToolDefinition> tools,
      CancellationToken ct)
  {
    var wire = new List<WireMessage>();

    // No dedicated system field here — it's just the first message.
    if (!string.IsNullOrWhiteSpace(systemPrompt))
      wire.Add(new WireMessage { Role = "system", Content = systemPrompt });

    foreach (var message in messages)
      wire.AddRange(ToWire(message));

    var request = new WireRequest
    {
      Model = _model,
      Messages = wire,
      Tools = tools.Count == 0 ? null : tools.Select(t => new WireTool
      {
        Function = new WireFunctionDef
        {
          Name = t.Name,
          Description = t.Description,
          Parameters = t.Schema
        }
      }).ToList()
    };

    using var response = await _http.PostAsJsonAsync(
        "v1/chat/completions", request, OpenAiJson.Default.WireRequest, ct);

    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {body}");
    }

    var parsed = await response.Content.ReadFromJsonAsync(
        OpenAiJson.Default.WireResponse, ct)
        ?? throw new InvalidOperationException("Empty response body.");

    var choice = parsed.Choices.FirstOrDefault()?.Message
        ?? throw new InvalidOperationException("Response contained no choices.");

    var blocks = new List<ContentBlock>();

    if (!string.IsNullOrEmpty(choice.Content))
      blocks.Add(new TextBlock(choice.Content));

    foreach (var call in choice.ToolCalls ?? [])
    {
      // Unwrap the double encoding. Local models get this wrong often
      // enough that a defensive parse is worth it.
      JsonDocument doc;
      try
      {
        doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
      }
      catch (JsonException)
      {
        doc = JsonDocument.Parse("{}");
      }

      _retained.Add(doc);
      blocks.Add(new ToolUseBlock(call.Id, call.Function.Name, doc.RootElement));
    }

    return new Message(Role.Assistant, blocks);
  }

  private static IEnumerable<WireMessage> ToWire(Message message)
  {
    // One neutral message can fan out into several wire messages here,
    // which is precisely what the Anthropic adapter never has to do.
    if (message.Role == Role.Tool)
    {
      foreach (var result in message.Content.OfType<ToolResultBlock>())
        yield return new WireMessage
        {
          Role = "tool",
          ToolCallId = result.ToolUseId,
          Content = result.IsError ? $"ERROR: {result.Content}" : result.Content
        };
      yield break;
    }

    var text = string.Join("\n", message.Content.OfType<TextBlock>().Select(t => t.Text));
    var calls = message.Content.OfType<ToolUseBlock>().Select(u => new WireToolCall
    {
      Id = u.Id,
      Function = new WireFunctionCall
      {
        Name = u.Name,
        Arguments = u.Input.GetRawText()
      }
    }).ToList();

    yield return new WireMessage
    {
      Role = message.Role == Role.Assistant ? "assistant" : "user",
      Content = string.IsNullOrEmpty(text) ? null : text,
      ToolCalls = calls.Count == 0 ? null : calls
    };
  }
}