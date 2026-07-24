using System.Text.Json;

namespace RelayAgent;

// ---------------------------------------------------------------------------
// The provider-neutral model.
//
// This is the single most important design decision in a harness. Anthropic and
// OpenAI disagree about almost everything at the wire level:
//
//   - Anthropic messages carry a LIST of typed content blocks.
//     OpenAI messages carry a string plus a separate tool_calls array.
//   - Anthropic tool arguments arrive as a real JSON object.
//     OpenAI tool arguments arrive as a JSON-encoded *string*.
//   - Anthropic tool results are blocks inside a "user" message.
//     OpenAI tool results are their own messages with role "tool".
//
// If you let either wire format leak into the agent loop, you end up writing the
// loop twice. So the loop speaks only the types below, and each client adapter
// owns the translation in both directions.
// ---------------------------------------------------------------------------

public enum Role
{
  User,
  Assistant,
  /// <summary>Results being handed back to the model. Each provider encodes this differently.</summary>
  Tool
}

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>The model asking us to run a tool.</summary>
/// <param name="Id">Correlation id — the result must quote it back.</param>
/// <param name="Input">Always a JSON object, even when the provider sent a string.</param>
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

/// <summary>Our answer to a <see cref="ToolUseBlock"/>.</summary>
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock;

public sealed record Message(Role Role, IReadOnlyList<ContentBlock> Content)
{
  public static Message User(string text) => new(Role.User, [new TextBlock(text)]);

  public string TextContent =>
      string.Join("\n", Content.OfType<TextBlock>().Select(t => t.Text));
}

/// <summary>What the model is told a tool looks like.</summary>
/// <param name="Schema">A JSON Schema object describing the parameters.</param>
public sealed record ToolDefinition(string Name, string Description, JsonElement Schema);

public interface ILlmClient
{
  /// <summary>
  /// One round trip. Non-streaming on purpose — streaming is a presentation
  /// concern and adds SSE parsing noise before the loop itself is understood.
  /// </summary>
  Task<Message> CompleteAsync(
      IReadOnlyList<Message> messages,
      string systemPrompt,
      IReadOnlyList<ToolDefinition> tools,
      CancellationToken ct);
}