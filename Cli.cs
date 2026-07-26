using RelayAgent.Auth;

namespace RelayAgent.Cli;

public enum AuthAction
{
  Login,
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
        throw new ArgumentException("Usage: relay auth <login|status|logout>");

      var action = args[1] switch
      {
        "login" => AuthAction.Login,
        "status" => AuthAction.Status,
        "logout" => AuthAction.Logout,
        _ => throw new ArgumentException($"Unknown auth subcommand '{args[1]}'.")
      };

      return new RelayCommand.Auth(action);
    }

    return new RelayCommand.Workspace(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
  }
}

/// <summary>Console-facing glue: formats <see cref="AuthManager"/> results into relay's terse output.</summary>
public static class AuthCommands
{
  public static async Task<string> ExecuteAsync(AuthAction action, AuthManager authManager) => action switch
  {
    AuthAction.Login => await LoginAsync(authManager),
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

  private static async Task<string> LogoutAsync(AuthManager authManager)
  {
    await authManager.LogoutAsync();
    return "Logged out.";
  }

  private static string FormatStatus(AuthStatus status) => status switch
  {
    { SignedIn: true, ExpiresIn: { } remaining } => $"Signed in (expires in {FormatDuration(remaining)})",
    _ => "Not signed in — run 'relay auth login'"
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
