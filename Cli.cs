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

/// <summary>Stub handlers — no real auth behavior lands until later tickets.</summary>
public static class AuthCommands
{
  public static string Execute(AuthAction action) => action switch
  {
    AuthAction.Login => "auth login: not implemented yet.",
    AuthAction.Status => "auth status: not implemented yet.",
    AuthAction.Logout => "auth logout: not implemented yet.",
    _ => throw new ArgumentOutOfRangeException(nameof(action))
  };
}
