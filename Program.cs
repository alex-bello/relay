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
var backend = (Environment.GetEnvironmentVariable("RELAY_BACKEND") ?? "anthropic").ToLowerInvariant();

// One AuthManager for the chatgpt backend, shared by the startup login gate, the mid-session
// recovery prompt, and the per-request credential source. Null for every other backend.
var chatGptAuth = backend == "chatgpt"
    ? new AuthManager(http, TimeProvider.System, AuthManager.DefaultPath())
    : null;

ILlmClient client = backend switch
{
  "anthropic" => new AnthropicClient(
      http,
      Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
          ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set."),
      Environment.GetEnvironmentVariable("RELAY_MODEL") ?? "claude-sonnet-4-5"),

  "openai" or "local" => new OpenAiClient(
      http,
      new Uri(Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "http://vm-llm:8080/"),
      new StaticCredentialSource(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
      Environment.GetEnvironmentVariable("RELAY_MODEL") ?? "local-model"),

  // The ChatGPT sign-in token is not a platform API key: it authenticates against ChatGPT's own
  // Responses backend, which also wants the account id in a header. See ChatGptResponsesClient.
  "chatgpt" => new ChatGptResponsesClient(
      http,
      new Uri("https://chatgpt.com/backend-api/codex/"),
      async ct =>
      {
        var token = await chatGptAuth!.GetFreshAccessTokenAsync(ct);
        var stored = await chatGptAuth.ReadChatGptAsync(ct);
        return new ChatGptRequestCredentials(
            token,
            stored?.AccountId ?? throw new InvalidOperationException(
                "ChatGPT session has no account id; run `relay auth login` again."));
      },
      Environment.GetEnvironmentVariable("RELAY_MODEL") ?? "gpt-5"),

  _ => throw new InvalidOperationException($"Unknown backend '{backend}'.")
};

// Thin glue: the login gate owns the fail-fast-vs-prompt decision; this only wires the real console
// and auth calls into it. A no-TTY-or-declined startup exits non-zero before the REPL opens.
// "Signed in" means we can produce a working token, not merely that an unexpired access token is
// on disk: a lapsed access token with a live refresh token is a valid session, and this refreshes
// it exactly as the first turn would. Only a genuinely dead session (NotSignedInException) gates;
// a transient endpoint error propagates rather than masquerading as signed-out.
var loginGate = chatGptAuth is null ? null : new ChatGptLoginGate(
    new ConsoleTerminal(),
    async ct =>
    {
      try
      {
        await chatGptAuth.GetFreshAccessTokenAsync(ct);
        return true;
      }
      catch (NotSignedInException)
      {
        return false;
      }
    },
    chatGptAuth.LoginAsync);

if (loginGate is not null && !await loginGate.EnsureSignedInAsync())
  return 1;

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
  catch (NotSignedInException) when (loginGate is not null)
  {
    // A refresh failed mid-turn: the in-flight turn is lost. Offer an inline re-login, then fall
    // back to the `›` prompt for the user to retype — no auto-retry of the interrupted turn, so no
    // pending-turn state or double-executed tool calls survive the login interruption.
    Console.Error.WriteLine();
    await loginGate.PromptAndLoginAsync();
    Console.WriteLine();
  }
  catch (Exception ex)
  {
    Console.Error.WriteLine($"\n{ex.Message}\n");
  }
}

return 0;