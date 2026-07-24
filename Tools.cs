using System.Text.Json;

namespace RelayAgent.Tools;

public interface ITool
{
  string Name { get; }
  string Description { get; }

  /// <summary>
  /// JSON Schema for the arguments, as a literal string.
  ///
  /// Hand-written on purpose. The reflection-based schema generators you'd
  /// normally reach for are exactly what Native AOT forbids. When this gets
  /// tedious, the fix is a source generator that emits the schema at compile
  /// time from your parameter record — that's the natural next milestone.
  /// </summary>
  string SchemaJson { get; }

  Task<string> ExecuteAsync(JsonElement input, CancellationToken ct);
}

public sealed class ToolRegistry
{
  private readonly Dictionary<string, ITool> _tools;
  private readonly List<ToolDefinition> _definitions;
  private readonly List<JsonDocument> _schemaDocs = [];

  public ToolRegistry(IEnumerable<ITool> tools)
  {
    _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    _definitions = [];

    foreach (var tool in _tools.Values)
    {
      // JsonDocument.Parse is fully AOT-safe: it is a reader, not a
      // reflection-based deserializer. We hold the documents alive because
      // JsonElement is a view into its parent document's buffer.
      var doc = JsonDocument.Parse(tool.SchemaJson);
      _schemaDocs.Add(doc);
      _definitions.Add(new ToolDefinition(tool.Name, tool.Description, doc.RootElement));
    }
  }

  public IReadOnlyList<ToolDefinition> Definitions => _definitions;

  public async Task<ToolResultBlock> InvokeAsync(ToolUseBlock use, CancellationToken ct)
  {
    if (!_tools.TryGetValue(use.Name, out var tool))
      return new ToolResultBlock(use.Id, $"No tool named '{use.Name}'.", IsError: true);

    try
    {
      var result = await tool.ExecuteAsync(use.Input, ct);
      return new ToolResultBlock(use.Id, result);
    }
    catch (Exception ex)
    {
      // Errors go back to the MODEL, not up the stack. Letting the model
      // see "file not found" and retry with a corrected path is most of
      // what makes an agent feel agentic. Throwing here ends the turn and
      // wastes everything the model had figured out.
      return new ToolResultBlock(use.Id, $"{ex.GetType().Name}: {ex.Message}", IsError: true);
    }
  }
}

public sealed class ReadFileTool(string rootDirectory) : ITool
{
  private readonly string _root = Path.GetFullPath(rootDirectory);

  public string Name => "read_file";

  public string Description =>
      "Read a UTF-8 text file from the workspace. Paths are relative to the workspace root.";

  public string SchemaJson => """
    {
      "type": "object",
      "properties": {
        "path": {
          "type": "string",
          "description": "Path to the file, relative to the workspace root."
        }
      },
      "required": ["path"],
      "additionalProperties": false
    }
    """;

  public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct)
  {
    if (!input.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
      throw new ArgumentException("Expected a string property 'path'.");

    var requested = Path.GetFullPath(Path.Combine(_root, pathProp.GetString()!));

    // Never trust a path the model produced. Traversal out of the workspace
    // is the single most common way a toy harness becomes a liability.
    if (!requested.StartsWith(_root, StringComparison.Ordinal))
      throw new UnauthorizedAccessException("Path escapes the workspace root.");

    if (!File.Exists(requested))
      throw new FileNotFoundException($"No such file: {pathProp.GetString()}");

    var text = await File.ReadAllTextAsync(requested, ct);

    // Context budget is a real resource. Truncate loudly rather than
    // silently blowing the window on one file.
    const int limit = 60_000;
    return text.Length <= limit
        ? text
        : text[..limit] + $"\n\n[truncated: {text.Length - limit} more characters]";
  }
}