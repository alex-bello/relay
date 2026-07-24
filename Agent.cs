using RelayAgent.Tools;

namespace RelayAgent;

/// <summary>
/// The whole idea, in about forty lines.
///
/// An agent is a while-loop over a growing list of messages. That list IS the
/// agent's entire mind: the model is stateless and re-reads all of it on every
/// single request. Nothing persists between calls except what you put in
/// <see cref="_transcript"/>. Once that clicks, most agent behaviour stops being
/// mysterious — "it forgot" means it fell out of the window, "it looped" means
/// the transcript kept telling it to.
/// </summary>
public sealed class Agent(ILlmClient client, ToolRegistry tools, string systemPrompt)
{
  private readonly List<Message> _transcript = [];

  /// <summary>Stops a malformed tool or a stubborn model from burning your budget.</summary>
  public int MaxStepsPerTurn { get; init; } = 25;

  public IReadOnlyList<Message> Transcript => _transcript;

  public event Action<ToolUseBlock>? ToolInvoked;

  public async Task<string> RunTurnAsync(string userInput, CancellationToken ct = default)
  {
    _transcript.Add(Message.User(userInput));

    for (var step = 0; step < MaxStepsPerTurn; step++)
    {
      var reply = await client.CompleteAsync(_transcript, systemPrompt, tools.Definitions, ct);
      _transcript.Add(reply);

      var requests = reply.Content.OfType<ToolUseBlock>().ToList();

      // No tool calls means the model is done talking to itself and is
      // now talking to you. That's the only exit condition.
      if (requests.Count == 0)
        return reply.TextContent;

      var results = new List<ContentBlock>(requests.Count);
      foreach (var request in requests)
      {
        ToolInvoked?.Invoke(request);
        results.Add(await tools.InvokeAsync(request, ct));
      }

      // Every tool call must get a result appended before the next request,
      // or the providers will reject the transcript as malformed.
      _transcript.Add(new Message(Role.Tool, results));
    }

    return $"[stopped after {MaxStepsPerTurn} steps without a final answer]";
  }
}