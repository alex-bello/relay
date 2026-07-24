using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayAgent.Anthropic;

// ---------------------------------------------------------------------------
// Wire types. These exist only to be serialized; nothing outside this file
// should ever see them.
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WireText), "text")]
[JsonDerivedType(typeof(WireToolUse), "tool_use")]
[JsonDerivedType(typeof(WireToolResult), "tool_result")]
internal abstract class WireContent;

internal sealed class WireText : WireContent
{
  [JsonPropertyName("text")] public required string Text { get; set; }
}

internal sealed class WireToolUse : WireContent
{
  [JsonPropertyName("id")] public required string Id { get; set; }
  [JsonPropertyName("name")] public required string Name { get; set; }
  [JsonPropertyName("input")] public JsonElement Input { get; set; }
}

internal sealed class WireToolResult : WireContent
{
  [JsonPropertyName("tool_use_id")] public required string ToolUseId { get; set; }
  [JsonPropertyName("content")] public required string Content { get; set; }
  [JsonPropertyName("is_error")] public bool? IsError { get; set; }
}

internal sealed class WireMessage
{
  [JsonPropertyName("role")] public required string Role { get; set; }
  [JsonPropertyName("content")] public required List<WireContent> Content { get; set; }
}

internal sealed class WireTool
{
  [JsonPropertyName("name")] public required string Name { get; set; }
  [JsonPropertyName("description")] public required string Description { get; set; }
  [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }
}

internal sealed class WireRequest
{
  [JsonPropertyName("model")] public required string Model { get; set; }
  [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 8192;
  [JsonPropertyName("system")] public string? System { get; set; }
  [JsonPropertyName("messages")] public required List<WireMessage> Messages { get; set; }
  [JsonPropertyName("tools")] public List<WireTool>? Tools { get; set; }
}

internal sealed class WireResponse
{
  [JsonPropertyName("content")] public List<WireContent> Content { get; set; } = [];
  [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
}

// The AOT contract. Every type that crosses the serializer must be reachable
// from here, or you get a runtime throw thanks to the csproj switch.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(WireResponse))]
internal sealed partial class AnthropicJson : JsonSerializerContext;

// ---------------------------------------------------------------------------

public sealed class AnthropicClient : ILlmClient
{
  private readonly HttpClient _http;
  private readonly string _model;

  public AnthropicClient(HttpClient http, string apiKey, string model)
  {
    _http = http;
    _model = model;
    _http.BaseAddress ??= new Uri("https://api.anthropic.com/");
    _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
  }

  public async Task<Message> CompleteAsync(
      IReadOnlyList<Message> messages,
      string systemPrompt,
      IReadOnlyList<ToolDefinition> tools,
      CancellationToken ct)
  {
    var request = new WireRequest
    {
      Model = _model,
      System = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
      Messages = messages.Select(ToWire).ToList(),
      Tools = tools.Count == 0 ? null : tools.Select(t => new WireTool
      {
        Name = t.Name,
        Description = t.Description,
        InputSchema = t.Schema
      }).ToList()
    };

    using var response = await _http.PostAsJsonAsync(
        "v1/messages", request, AnthropicJson.Default.WireRequest, ct);

    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new HttpRequestException($"Anthropic {(int)response.StatusCode}: {body}");
    }

    var parsed = await response.Content.ReadFromJsonAsync(
        AnthropicJson.Default.WireResponse, ct)
        ?? throw new InvalidOperationException("Empty response body.");

    return new Message(Role.Assistant, parsed.Content.Select(FromWire).ToList());
  }

  private static WireMessage ToWire(Message message) => new()
  {
    // Note the mapping: tool results ride inside a *user* message here.
    Role = message.Role == Role.Assistant ? "assistant" : "user",
    Content = message.Content.Select<ContentBlock, WireContent>(block => block switch
    {
      TextBlock t => new WireText { Text = t.Text },
      ToolUseBlock u => new WireToolUse { Id = u.Id, Name = u.Name, Input = u.Input },
      ToolResultBlock r => new WireToolResult
      {
        ToolUseId = r.ToolUseId,
        Content = r.Content,
        IsError = r.IsError ? true : null
      },
      _ => throw new NotSupportedException(block.GetType().Name)
    }).ToList()
  };

  private static ContentBlock FromWire(WireContent content) => content switch
  {
    WireText t => new TextBlock(t.Text),
    WireToolUse u => new ToolUseBlock(u.Id, u.Name, u.Input),
    _ => throw new NotSupportedException(content.GetType().Name)
  };
}