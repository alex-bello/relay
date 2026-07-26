using RelayAgent.Cli;

namespace RelayAgent.Tests;

public class CommandParserTests
{
  [Fact]
  public void No_args_resolves_to_workspace_at_current_directory()
  {
    var result = CommandParser.Parse([]);

    var workspace = Assert.IsType<RelayCommand.Workspace>(result);
    Assert.Equal(Directory.GetCurrentDirectory(), workspace.Path);
  }

  [Fact]
  public void Path_argument_resolves_to_workspace_at_that_path()
  {
    var result = CommandParser.Parse(["/some/workspace"]);

    var workspace = Assert.IsType<RelayCommand.Workspace>(result);
    Assert.Equal("/some/workspace", workspace.Path);
  }

  [Theory]
  [InlineData("login", AuthAction.Login)]
  [InlineData("status", AuthAction.Status)]
  [InlineData("logout", AuthAction.Logout)]
  public void Auth_prefix_dispatches_to_the_named_subcommand(string subcommand, AuthAction expected)
  {
    var result = CommandParser.Parse(["auth", subcommand]);

    var auth = Assert.IsType<RelayCommand.Auth>(result);
    Assert.Equal(expected, auth.Action);
  }

  [Fact]
  public void Auth_with_no_subcommand_throws()
  {
    Assert.Throws<ArgumentException>(() => CommandParser.Parse(["auth"]));
  }

  [Fact]
  public void Auth_with_unknown_subcommand_throws()
  {
    Assert.Throws<ArgumentException>(() => CommandParser.Parse(["auth", "bogus"]));
  }

  [Fact]
  public void Explicit_path_named_auth_is_treated_as_a_workspace_not_the_subcommand()
  {
    var result = CommandParser.Parse(["./auth"]);

    var workspace = Assert.IsType<RelayCommand.Workspace>(result);
    Assert.Equal("./auth", workspace.Path);
  }
}

public class AuthCommandsTests
{
  [Theory]
  [InlineData(AuthAction.Login)]
  [InlineData(AuthAction.Status)]
  [InlineData(AuthAction.Logout)]
  public void Execute_returns_a_not_implemented_placeholder(AuthAction action)
  {
    var message = AuthCommands.Execute(action);

    Assert.Contains("not implemented", message, StringComparison.OrdinalIgnoreCase);
  }
}
