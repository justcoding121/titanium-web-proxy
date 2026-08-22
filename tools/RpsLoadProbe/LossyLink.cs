using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Userspace TCP delay + connection-stall shim (H1/H2). Does not drop bytes (that would corrupt HTTP);
/// instead applies per-buffer delay and occasional whole-connection stalls that hit all multiplexed streams.
/// </summary>
internal sealed class LossyTcpLink : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly IPEndPoint backend;
    private readonly int delayMs;
    private readonly double lossPercent;
    private readonly CancellationTokenSource cts = new();
    private readonly Random random = new();
    private Task? acceptLoop;

    public int Port { get; }

    private LossyTcpLink(TcpListener listener, IPEndPoint backend, int delayMs, double lossPercent)
    {
        this.listener = listener;
        this.backend = backend;
        this.delayMs = delayMs;
        this.lossPercent = lossPercent;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static LossyTcpLink Start(Uri backendUri, int delayMs, double lossPercent)
    {
        var backend = new IPEndPoint(IPAddress.Loopback, backendUri.Port);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var link = new LossyTcpLink(listener, backend, delayMs, lossPercent);
        link.acceptLoop = link.AcceptLoopAsync();
        return link;
    }

    public string ListenUrlForScheme(string scheme) => $"{scheme}://127.0.0.1:{Port}/";

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var backendClient = new TcpClient();
                await backendClient.ConnectAsync(backend, cts.Token);
                var stall = ShouldStall();
                var c2b = PumpAsync(client.GetStream(), backendClient.GetStream(), stall, cts.Token);
                var b2c = PumpAsync(backendClient.GetStream(), client.GetStream(), stall: false, cts.Token);
                await Task.WhenAny(c2b, b2c);
            }
            catch
            {
                // connection closed / cancelled
            }
        }
    }

    private bool ShouldStall()
    {
        if (lossPercent <= 0)
            return false;
        lock (random)
            return random.NextDouble() * 100.0 < lossPercent;
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool stall, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var stalled = false;
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await from.ReadAsync(buffer, ct);
            }
            catch
            {
                return;
            }

            if (read <= 0)
                return;

            if (delayMs > 0)
                await Task.Delay(delayMs, ct);

            if (stall && !stalled)
            {
                stalled = true;
                await Task.Delay(150, ct);
            }

            try
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try
        {
            listener.Stop();
        }
        catch
        {
            // ignore
        }

        if (acceptLoop != null)
        {
            try
            {
                await acceptLoop;
            }
            catch
            {
                // ignore
            }
        }

        cts.Dispose();
    }
}

/// <summary>
/// Userspace UDP delay + datagram-drop shim for HTTP/3 / QUIC.
/// Per-client ephemeral sockets demux replies back to the correct peer.
/// Delays are scheduled off the receive loops so MsQuic keeps pacing.
/// </summary>
internal sealed class LossyUdpLink : IAsyncDisposable
{
    private readonly UdpClient listener;
    private readonly IPEndPoint backend;
    private readonly int delayMs;
    private readonly double lossPercent;
    private readonly CancellationTokenSource cts = new();
    private readonly Random random = new();
    private readonly ConcurrentDictionary<string, UdpClient> clientSockets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> relayStarted = new(StringComparer.Ordinal);
    private Task? loop;

    public int Port { get; }

    private LossyUdpLink(UdpClient listener, IPEndPoint backend, int delayMs, double lossPercent)
    {
        this.listener = listener;
        this.backend = backend;
        this.delayMs = delayMs;
        this.lossPercent = lossPercent;
        Port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }

    public static LossyUdpLink Start(int backendPort, int delayMs, double lossPercent)
    {
        // IPv4 loopback only — dual-stack + localhost (::1) broke MsQuic on windows-latest GHA.
        var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var link = new LossyUdpLink(listener, new IPEndPoint(IPAddress.Loopback, backendPort), delayMs,
            lossPercent);
        link.loop = link.AcceptLoopAsync();
        return link;
    }

    public string ListenUrlHttps => $"https://127.0.0.1:{Port}/";

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await listener.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var key = result.RemoteEndPoint.ToString() ?? "unknown";
            var clientEp = result.RemoteEndPoint;
            var socket = clientSockets.GetOrAdd(key, static _ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));
            if (relayStarted.TryAdd(key, 0))
                _ = RelayBackendToClientAsync(socket, clientEp);

            if (ShouldDrop())
                continue;

            // Clone: ReceiveAsync may reuse buffers; delay is scheduled off this loop.
            var payload = (byte[])result.Buffer.Clone();
            _ = ForwardAsync(socket, payload, backend, cts.Token);
        }
    }

    private async Task RelayBackendToClientAsync(UdpClient socket, IPEndPoint client)
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (ShouldDrop())
                continue;

            var payload = (byte[])result.Buffer.Clone();
            _ = ForwardAsync(listener, payload, client, cts.Token);
        }
    }

    private async Task ForwardAsync(UdpClient socket, byte[] payload, IPEndPoint destination,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
            await socket.SendAsync(payload, destination, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }
    }

    private bool ShouldDrop()
    {
        if (lossPercent <= 0)
            return false;
        lock (random)
            return random.NextDouble() * 100.0 < lossPercent;
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try
        {
            listener.Dispose();
        }
        catch
        {
            // ignore
        }

        foreach (var kv in clientSockets)
        {
            try
            {
                kv.Value.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        if (loop != null)
        {
            try
            {
                await loop;
            }
            catch
            {
                // ignore
            }
        }

        cts.Dispose();
    }
}
