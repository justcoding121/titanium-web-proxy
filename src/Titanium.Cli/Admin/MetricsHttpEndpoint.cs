using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Cli.Admin;

/// <summary>
///     Minimal <c>GET /metrics</c> listener for <c>server.metrics.enabled</c> without the admin server.
///     One request per connection, no keep-alive, request line only. Loopback by default.
/// </summary>
internal sealed class MetricsHttpEndpoint : IDisposable
{
    public const int DefaultPort = 9464;
    public const string DefaultHost = "127.0.0.1";

    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly MetricsExposition _exposition;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public MetricsHttpEndpoint(MetricsExposition exposition, IPAddress address, int port)
    {
        _exposition = exposition;
        _listener = new TcpListener(address, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static MetricsHttpEndpoint Create(MetricsExposition exposition, string? host, int? port) =>
        new(exposition, IPAddress.Parse(string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim()), port ?? DefaultPort);

    public void Start()
    {
        _exposition.Start();
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync, _stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (AggregateException)
        {
            // Accept loop ends with an exception once the listener stops.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client), _stop.Token);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                var (status, body, includeBody) = Respond(head);
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {PrometheusTextWriter.ContentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                if (includeBody)
                {
                    await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A scraper that goes away mid-request is not an error.
            }
        }
    }

    private (string Status, string Body, bool IncludeBody) Respond(string head)
    {
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var parts = (lineEnd < 0 ? head : head[..lineEnd]).Split(' ');
        if (parts.Length < 2)
        {
            return ("400 Bad Request", "bad request\n", true);
        }

        var isHead = string.Equals(parts[0], "HEAD", StringComparison.Ordinal);
        if (!isHead && !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            return ("405 Method Not Allowed", "method not allowed\n", true);
        }

        var path = parts[1];
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        return string.Equals(path, "/metrics", StringComparison.Ordinal)
            ? ("200 OK", _exposition.Render(), !isHead)
            : ("404 Not Found", "not found\n", true);
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }
}
