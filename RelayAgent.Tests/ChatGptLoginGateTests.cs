using RelayAgent.Auth;
using RelayAgent.Cli;

namespace RelayAgent.Tests;

/// <summary>
/// Exercises ticket #19's startup and mid-REPL login gate: the fail-fast path for a redirected
/// (non-interactive) terminal, the interactive y/n prompt, the shared "identical exit" that a
/// declined "n" and a no-TTY both take, and the inline-login-then-proceed path. The terminal and
/// the login/status calls are faked, so no real console or browser is involved.
/// </summary>
public class ChatGptLoginGateTests
{
  [Fact]
  public async Task EnsureSignedIn_proceeds_without_prompting_when_already_signed_in()
  {
    var terminal = new FakeTerminal { IsInteractive = true };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(true), FailIfCalled);

    Assert.True(await gate.EnsureSignedInAsync());
    Assert.False(terminal.ConfirmWasCalled);
    Assert.Null(terminal.LastError);
  }

  [Fact]
  public async Task EnsureSignedIn_fails_fast_without_prompting_when_not_interactive_and_no_token()
  {
    var terminal = new FakeTerminal { IsInteractive = false };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(false), FailIfCalled);

    Assert.False(await gate.EnsureSignedInAsync());
    Assert.False(terminal.ConfirmWasCalled);
    Assert.Equal(AuthManager.NotSignedInMessage, terminal.LastError);
  }

  [Fact]
  public async Task EnsureSignedIn_prompts_when_interactive_with_no_token()
  {
    var terminal = new FakeTerminal { IsInteractive = true, ConfirmAnswer = false };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(false), FailIfCalled);

    await gate.EnsureSignedInAsync();

    Assert.Equal("sign in with ChatGPT now?", terminal.LastQuestion);
  }

  [Fact]
  public async Task Declining_the_prompt_takes_the_identical_exit_path_as_no_tty()
  {
    var terminal = new FakeTerminal { IsInteractive = true, ConfirmAnswer = false };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(false), FailIfCalled);

    Assert.False(await gate.EnsureSignedInAsync());
    Assert.Equal(AuthManager.NotSignedInMessage, terminal.LastError);
  }

  [Fact]
  public async Task Accepting_the_prompt_runs_the_login_and_proceeds()
  {
    var loginRan = false;
    var terminal = new FakeTerminal { IsInteractive = true, ConfirmAnswer = true };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(false), _ =>
    {
      loginRan = true;
      return Task.CompletedTask;
    });

    Assert.True(await gate.EnsureSignedInAsync());
    Assert.True(loginRan);
    Assert.Null(terminal.LastError);
  }

  [Fact]
  public async Task A_failed_login_surfaces_its_reason_and_does_not_proceed()
  {
    var terminal = new FakeTerminal { IsInteractive = true, ConfirmAnswer = true };
    var gate = new ChatGptLoginGate(terminal, _ => Task.FromResult(false),
        _ => throw new InvalidOperationException("browser was cancelled"));

    Assert.False(await gate.EnsureSignedInAsync());
    Assert.Contains("browser was cancelled", terminal.LastError);
  }

  [Fact]
  public async Task PromptAndLogin_prompts_directly_without_a_status_check_for_mid_repl_recovery()
  {
    // Mid-REPL recovery skips the signed-in check (the refresh already failed) and prompts straight
    // away; an affirmative re-login returns control so the REPL can go back to its prompt.
    var loginRan = false;
    var terminal = new FakeTerminal { IsInteractive = true, ConfirmAnswer = true };
    var gate = new ChatGptLoginGate(terminal, _ => throw new InvalidOperationException("must not be consulted"),
        _ => { loginRan = true; return Task.CompletedTask; });

    Assert.True(await gate.PromptAndLoginAsync());
    Assert.True(loginRan);
  }

  private static Task FailIfCalled(CancellationToken ct) =>
      throw new InvalidOperationException("login should not have been attempted");

  private sealed class FakeTerminal : ILoginTerminal
  {
    public bool IsInteractive { get; init; }
    public bool ConfirmAnswer { get; init; }

    public bool ConfirmWasCalled { get; private set; }
    public string? LastQuestion { get; private set; }
    public string? LastError { get; private set; }

    public bool Confirm(string question)
    {
      ConfirmWasCalled = true;
      LastQuestion = question;
      return ConfirmAnswer;
    }

    public void WriteError(string message) => LastError = message;
  }
}
