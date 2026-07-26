using System.Net;
using System.Net.Sockets;
using RelayAgent.Auth;

namespace RelayAgent.Tests;

public class LoopbackCallbackServerTests
{
  private static readonly HttpClient Http = new();
  private static readonly TimeSpan FastRetryInterval = TimeSpan.FromMilliseconds(10);

  [Fact]
  public async Task StartAsync_binds_the_default_port_and_exposes_the_localhost_redirect_uri()
  {
    using var server = await LoopbackCallbackServer.StartAsync(Http);

    Assert.Equal(LoopbackCallbackServer.DefaultPort, server.Port);
    Assert.Equal($"http://localhost:{LoopbackCallbackServer.DefaultPort}/auth/callback", server.RedirectUri.ToString());
  }

  [Fact]
  public async Task WaitForCallbackAsync_returns_the_callback_query_parameters()
  {
    using var server = await LoopbackCallbackServer.StartAsync(Http);
    var waitTask = server.WaitForCallbackAsync();

    using var response = await Http.GetAsync(new Uri(server.RedirectUri, "?code=abc123&state=xyz"));

    var query = await waitTask;
    Assert.NotNull(query);
    Assert.Equal("abc123", query["code"]);
    Assert.Equal("xyz", query["state"]);
  }

  [Fact]
  public async Task WaitForCallbackAsync_returns_null_when_cancel_is_requested()
  {
    using var server = await LoopbackCallbackServer.StartAsync(Http);
    var waitTask = server.WaitForCallbackAsync();

    using var response = await Http.GetAsync($"http://localhost:{server.Port}/cancel");

    var query = await waitTask;
    Assert.Null(query);
  }

  [Fact]
  public async Task StartAsync_frees_a_stale_listener_via_cancel_and_takes_over_the_port()
  {
    var staleServer = await LoopbackCallbackServer.StartAsync(Http, FastRetryInterval, maxAttempts: 10, ct: default);
    var staleWaitTask = Task.Run(async () =>
    {
      var result = await staleServer.WaitForCallbackAsync();
      staleServer.Dispose();
      return result;
    });

    using var newServer = await LoopbackCallbackServer.StartAsync(Http, FastRetryInterval, maxAttempts: 10, ct: default);

    Assert.Equal(LoopbackCallbackServer.DefaultPort, newServer.Port);
    Assert.Null(await staleWaitTask);
  }

  [Fact]
  public async Task StartAsync_falls_back_to_the_secondary_port_when_the_default_is_unavailable()
  {
    using var blocker = new TcpListener(IPAddress.Loopback, LoopbackCallbackServer.DefaultPort);
    blocker.Start();

    using var server = await LoopbackCallbackServer.StartAsync(Http, FastRetryInterval, maxAttempts: 3, ct: default);

    Assert.Equal(LoopbackCallbackServer.FallbackPort, server.Port);
  }

  [Fact]
  public async Task StartAsync_throws_a_clear_error_when_both_ports_are_unavailable()
  {
    using var blockerDefault = new TcpListener(IPAddress.Loopback, LoopbackCallbackServer.DefaultPort);
    blockerDefault.Start();
    using var blockerFallback = new TcpListener(IPAddress.Loopback, LoopbackCallbackServer.FallbackPort);
    blockerFallback.Start();

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        LoopbackCallbackServer.StartAsync(Http, FastRetryInterval, maxAttempts: 3, ct: default));

    Assert.Contains("1455", ex.Message);
    Assert.Contains("1457", ex.Message);
  }
}
