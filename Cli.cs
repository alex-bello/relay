using RelayAgent.Auth;

namespace RelayAgent.Cli;

public enum AuthAction
{
  Login,
  LoginDeviceCode,
  Status,
  Logout
}

// ---------------------------------------------------------------------------
// What relay's command line resolved to. Kept separate from argument PARSING
// so Program.cs can switch on an intent instead of re-deriving one from args.
// ---------------------------------------------------------------------------
public abstract record RelayCommand
{
  public sealed record Workspace(string Path) : RelayCommand;

  public sealed record Auth(AuthAction Action) : RelayCommand;
}

public static class CommandParser
{
  /// <summary>
  /// `auth` is a reserved subcommand prefix, unconditionally — never
  /// conditional on whether a directory named "auth" exists on disk. A
  /// workspace literally named auth still works via an explicit path
  /// (`relay ./auth`), the same disambiguation git/gh/docker use.
  /// </summary>
  public static RelayCommand Parse(string[] args)
  {
    if (args.Length > 0 && args[0] == "auth")
    {
      if (args.Length < 2)
        throw new ArgumentException("Usage: relay auth <login [--device-code]|status|logout>");

      var action = args[1] switch
      {
        "login" => ParseLoginAction(args),
        "status" => AuthAction.Status,
        "logout" => AuthAction.Logout,
        _ => throw new ArgumentException($"Unknown auth subcommand '{args[1]}'.")
      };

      return new RelayCommand.Auth(action);
    }

    return new RelayCommand.Workspace(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
  }

  private static AuthAction ParseLoginAction(string[] args)
  {
    if (args.Length == 2)
      return AuthAction.Login;

    if (args.Length == 3 && args[2] == "--device-code")
      return AuthAction.LoginDeviceCode;

    throw new ArgumentException($"Unknown option '{args[2]}' for 'relay auth login'.");
  }
}

/// <summary>
/// The terminal interactions the login gate needs, abstracted so the gate's branch logic can be
/// unit-tested without a real TTY. Production wires <see cref="ConsoleTerminal"/> over
/// <see cref="System.Console"/>; tests substitute a fake.
/// </summary>
public interface ILoginTerminal
{
  /// <summary>False when stdin or stdout is redirected — no human at the keyboard to answer a prompt.</summary>
  bool IsInteractive { get; }

  /// <summary>Prompts <paramref name="question"/> as a yes/no and returns true only for an affirmative answer.</summary>
  bool Confirm(string question);

  /// <summary>Writes a line to stderr.</summary>
  void WriteError(string message);
}

/// <summary>Production <see cref="ILoginTerminal"/>: the thin, manually-tested glue over <see cref="Console"/>.</summary>
public sealed class ConsoleTerminal : ILoginTerminal
{
  // Either stream being redirected means there's no interactive terminal to prompt at — matching
  // the ticket's "Console.IsInputRedirected or Console.IsOutputRedirected being true counts as no TTY."
  public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

  public bool Confirm(string question)
  {
    Console.Write($"{question} y/n ");
    var answer = Console.ReadLine()?.Trim();
    return answer?.StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
  }

  public void WriteError(string message) => Console.Error.WriteLine(message);
}

/// <summary>
/// Decides what happens when the chatgpt backend needs a token it doesn't have — at startup and
/// after a mid-session refresh failure. Terminal I/O and the login/status calls are injected, so the
/// branch logic here is unit-testable without a real console or browser. The "no token and we're not
/// getting one right now" outcome is one code path: a redirected terminal and an explicit "n" both
/// take it, exactly as the ticket requires.
/// </summary>
public sealed class ChatGptLoginGate(
    ILoginTerminal terminal,
    Func<CancellationToken, Task<bool>> isSignedIn,
    Func<CancellationToken, Task> login)
{
  /// <summary>Startup gate: proceed if already signed in, otherwise offer an inline login. Returns true to proceed.</summary>
  public async Task<bool> EnsureSignedInAsync(CancellationToken ct = default) =>
      await isSignedIn(ct) || await PromptAndLoginAsync(ct);

  /// <summary>
  /// The shared "no valid token" recovery, used by both startup and mid-REPL. No TTY, or a declined
  /// prompt, both write the standard guidance and return false — never distinct messaging per cause.
  /// An affirmative runs the login flow inline; success returns true, a failed login surfaces its
  /// reason and returns false.
  /// </summary>
  public async Task<bool> PromptAndLoginAsync(CancellationToken ct = default)
  {
    if (!terminal.IsInteractive || !terminal.Confirm("sign in with ChatGPT now?"))
    {
      terminal.WriteError(AuthManager.NotSignedInMessage);
      return false;
    }

    try
    {
      await login(ct);
      return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      terminal.WriteError($"Sign-in failed: {ex.Message}");
      return false;
    }
  }
}

/// <summary>Console-facing glue: formats <see cref="AuthManager"/> results into relay's terse output.</summary>
public static class AuthCommands
{
  public static async Task<string> ExecuteAsync(AuthAction action, AuthManager authManager) => action switch
  {
    AuthAction.Login => await LoginAsync(authManager),
    AuthAction.LoginDeviceCode => await LoginWithDeviceCodeAsync(authManager),
    AuthAction.Status => FormatStatus(await authManager.GetStatusAsync()),
    AuthAction.Logout => await LogoutAsync(authManager),
    _ => throw new ArgumentOutOfRangeException(nameof(action))
  };

  private static async Task<string> LoginAsync(AuthManager authManager)
  {
    Console.WriteLine("Opening a browser to sign in with ChatGPT...");
    await authManager.LoginAsync();
    return FormatStatus(await authManager.GetStatusAsync());
  }

  private static async Task<string> LoginWithDeviceCodeAsync(AuthManager authManager)
  {
    await authManager.LoginWithDeviceCodeAsync((userCode, verificationUrl) =>
    {
      Console.WriteLine($"Go to {verificationUrl} and enter code: {userCode}");
      Console.WriteLine("Waiting for you to finish signing in there...");
    });
    return FormatStatus(await authManager.GetStatusAsync());
  }

  private static async Task<string> LogoutAsync(AuthManager authManager)
  {
    await authManager.LogoutAsync();
    return "Logged out.";
  }

  private static string FormatStatus(AuthStatus status) => status switch
  {
    { SignedIn: true, ExpiresIn: { } remaining } => $"Signed in (expires in {FormatDuration(remaining)})",
    _ => AuthManager.NotSignedInMessage
  };

  private static string FormatDuration(TimeSpan remaining)
  {
    var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
    return totalMinutes < 60
        ? $"{totalMinutes}m"
        : totalMinutes % 60 == 0
            ? $"{totalMinutes / 60}h"
            : $"{totalMinutes / 60}h {totalMinutes % 60}m";
  }
}
