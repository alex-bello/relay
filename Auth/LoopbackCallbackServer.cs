using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RelayAgent.Auth;

/// <summary>
/// A fixed-port loopback HTTP listener that receives an OAuth redirect
/// callback (or an abandonment signal) and hands the raw query parameters
/// back to its caller. It knows nothing about PKCE or token exchange — that
/// lives in the flow built on top of it (ticket #15) — so it can be reused
/// by any future OAuth flow relay adds.
/// </summary>
public sealed class LoopbackCallbackServer : IDisposable
{
  public const int DefaultPort = 1455;
  public const int FallbackPort = 1457;

  private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(200);
  private const int DefaultMaxAttempts = 10;

  private readonly TcpListener _listener;

  public int Port { get; }

  /// <summary>
  /// Literal `localhost`, not `127.0.0.1`, even though the listener binds the
  /// loopback address directly — this must match the OAuth app's registered
  /// redirect-URI allow-list.
  /// </summary>
  public Uri RedirectUri => new($"http://localhost:{Port}/auth/callback");

  private LoopbackCallbackServer(TcpListener listener, int port)
  {
    _listener = listener;
    Port = port;
  }

  public static Task<LoopbackCallbackServer> StartAsync(HttpClient http, CancellationToken ct = default) =>
      StartAsync(http, DefaultRetryInterval, DefaultMaxAttempts, ct);

  /// <summary>
  /// Test seam: production always goes through the 200ms/10-attempt defaults
  /// above; tests shrink both so the fallback-port path doesn't cost ~2s of
  /// real wall-clock time per run.
  /// </summary>
  internal static async Task<LoopbackCallbackServer> StartAsync(
      HttpClient http, TimeSpan retryInterval, int maxAttempts, CancellationToken ct)
  {
    try
    {
      return new LoopbackCallbackServer(
          await BindWithRetryAsync(http, DefaultPort, retryInterval, maxAttempts, ct), DefaultPort);
    }
    catch (SocketException)
    {
      try
      {
        return new LoopbackCallbackServer(
            await BindWithRetryAsync(http, FallbackPort, retryInterval, maxAttempts, ct), FallbackPort);
      }
      catch (SocketException ex)
      {
        throw new InvalidOperationException(
            $"Could not start the OAuth callback listener: ports {DefaultPort} and {FallbackPort} " +
            "are both already in use.", ex);
      }
    }
  }

  private static async Task<TcpListener> BindWithRetryAsync(
      HttpClient http, int port, TimeSpan retryInterval, int maxAttempts, CancellationToken ct)
  {
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
      try
      {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
      }
      catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
      {
        // Assumed to be a stale prior login attempt still holding the port;
        // ask it to give up the port rather than waiting it out blindly.
        if (attempt == 1)
          await SendCancelBestEffortAsync(http, port, ct);

        if (attempt == maxAttempts)
          throw;

        await Task.Delay(retryInterval, ct);
      }
    }

    throw new UnreachableException();
  }

  private static async Task SendCancelBestEffortAsync(HttpClient http, int port, CancellationToken ct)
  {
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));

    try
    {
      using var response = await http.GetAsync($"http://127.0.0.1:{port}/cancel", timeoutCts.Token);
    }
    catch (HttpRequestException)
    {
      // Nothing valid answered — either it's not our kind of listener or it's
      // already gone. Either way the next bind attempt is the real signal.
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      // Our own 1s probe timeout elapsed, not the caller's cancellation.
    }
  }

  /// <summary>
  /// Blocks until a single meaningful request arrives: a callback (returns
  /// its query parameters) or a /cancel (returns null). There is deliberately
  /// no timeout here — an abandoned flow is ended by /cancel or by the
  /// caller's own cancellation (e.g. Ctrl-C), never by a clock. Any other
  /// request is answered with 404 and the wait continues.
  /// </summary>
  public async Task<IReadOnlyDictionary<string, string>?> WaitForCallbackAsync(CancellationToken ct = default)
  {
    while (true)
    {
      using var client = await _listener.AcceptTcpClientAsync(ct);
      await using var stream = client.GetStream();

      var request = await ReadRequestLineAsync(stream, ct);
      if (request is not { } value)
        continue;

      var (method, path, query) = value;

      if (method != "GET")
      {
        await WriteResponseAsync(stream, "404 Not Found", "Not found.", ct);
        continue;
      }

      switch (path)
      {
        case "/cancel":
          await WriteResponseAsync(stream, "200 OK", "Login cancelled. You can close this window.", ct);
          return null;
        case "/auth/callback":
          await WriteResponseAsync(stream, "200 OK", "Signed in. You can close this window.", ct);
          return query;
        default:
          await WriteResponseAsync(stream, "404 Not Found", "Not found.", ct);
          continue;
      }
    }
  }

  private static async Task<(string Method, string Path, Dictionary<string, string> Query)?> ReadRequestLineAsync(
      NetworkStream stream, CancellationToken ct)
  {
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    var line = await reader.ReadLineAsync(ct);
    if (string.IsNullOrEmpty(line))
      return null;

    var parts = line.Split(' ');
    if (parts.Length < 2)
      return null;

    var rawPath = parts[1];
    var queryIndex = rawPath.IndexOf('?');
    var path = queryIndex >= 0 ? rawPath[..queryIndex] : rawPath;
    var query = queryIndex >= 0 ? ParseQuery(rawPath[(queryIndex + 1)..]) : [];

    return (parts[0], path, query);
  }

  private static Dictionary<string, string> ParseQuery(string query)
  {
    var result = new Dictionary<string, string>();
    foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      var eq = pair.IndexOf('=');
      var key = eq >= 0 ? pair[..eq] : pair;
      var value = eq >= 0 ? pair[(eq + 1)..] : "";
      result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    return result;
  }

  private static async Task WriteResponseAsync(NetworkStream stream, string status, string body, CancellationToken ct)
  {
    var bodyBytes = Encoding.UTF8.GetBytes(body);
    var header =
        $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";

    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
    await stream.WriteAsync(bodyBytes, ct);
  }

  public void Dispose() => _listener.Stop();
}
