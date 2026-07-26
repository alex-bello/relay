using RelayAgent;
using RelayAgent.Anthropic;
using RelayAgent.Auth;
using RelayAgent.Cli;
using RelayAgent.OpenAI;
using RelayAgent.Tools;

RelayCommand command;
try
{
  command = CommandParser.Parse(args);
}
catch (ArgumentException ex)
{
  Console.Error.WriteLine(ex.Message);
  return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

if (command is RelayCommand.Auth auth)
{
  var authManager = new AuthManager(http, TimeProvider.System, AuthManager.DefaultPath());
  Console.WriteLine(await AuthCommands.ExecuteAsync(auth.Action, authManager));
  return 0;
}

if (command is not RelayCommand.Workspace workspaceCommand)
  throw new InvalidOperationException($"Unhandled command type '{command.GetType()}'.");

var workspace = workspaceCommand.Path;
var backend = Environment.GetEnvironmentVariable("RELAY_BACKEND") ?? "anthropic";

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

return 0;