using RelayAgent;
using RelayAgent.Anthropic;
using RelayAgent.OpenAI;
using RelayAgent.Tools;

var workspace = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var backend = Environment.GetEnvironmentVariable("RELAY_BACKEND") ?? "anthropic";

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

ILlmClient client = backend.ToLowerInvariant() switch
{
  "anthropic" => new AnthropicClient(
      http,
      Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
          ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set."),
      Environment.GetEnvironmentVariable("RELAY_MODEL") ?? "claude-sonnet-4-5"),

  "openai" or "local" => new OpenAiClient(
      http,
      new Uri(Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "http://vm-llm:8080/"),
      Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
      Environment.GetEnvironmentVariable("RELAY_MODEL") ?? "local-model"),

  _ => throw new InvalidOperationException($"Unknown backend '{backend}'.")
};

var tools = new ToolRegistry([new ReadFileTool(workspace)]);

var agent = new Agent(client, tools, $"""
    You are a terse coding assistant operating inside a workspace at {workspace}.
    Use the read_file tool to inspect files before answering questions about them.
    Never guess at file contents.
    """);

agent.ToolInvoked += use =>
{
  var previous = Console.ForegroundColor;
  Console.ForegroundColor = ConsoleColor.DarkGray;
  Console.WriteLine($"  ⟩ {use.Name} {use.Input.GetRawText()}");
  Console.ForegroundColor = previous;
};

Console.WriteLine($"relay · {backend} · {workspace}");
Console.WriteLine("Ctrl+C or an empty line to exit.\n");

while (true)
{
  Console.Write("› ");
  var input = Console.ReadLine();
  if (string.IsNullOrWhiteSpace(input)) break;

  try
  {
    var answer = await agent.RunTurnAsync(input);
    Console.WriteLine($"\n{answer}\n");
  }
  catch (Exception ex)
  {
    Console.Error.WriteLine($"\n{ex.Message}\n");
  }
}