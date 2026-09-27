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
/// <see cref="UdpClient"/> is not safe for concurrent <c>SendAsync</c>; each socket has a send gate.
/// Per-datagram delay is withheld for a short grace period after each client path starts so MsQuic
/// can finish the handshake (1-RTT short-header packets still carry HANDSHAKE_DONE); loss% applies
/// immediately. After the grace window, delay applies to all datagrams (models RTT for the body).
/// </summary>
internal sealed class LossyUdpLink : IAsyncDisposable
{
    /// <summary>No userspace delay for this long after the first client datagram (handshake / 1-RTT settle).</summary>
    private const int DelayGraceMs = 500;

    private readonly UdpClient listener;
    private readonly SemaphoreSlim listenerSendGate = new(1, 1);
    private readonly IPEndPoint backend;
    private readonly int delayMs;
    private readonly double lossPercent;
    private readonly CancellationTokenSource cts = new();
    private readonly Random random = new();
    private readonly ConcurrentDictionary<string, ClientRelay> relays = new(StringComparer.Ordinal);
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
        listener.Client.ReceiveBufferSize = 1 << 20;
        listener.Client.SendBufferSize = 1 << 20;
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
            if (!relays.TryGetValue(key, out var relay))
            {
                var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                // Larger recv buffer: delayed client→origin forward gives the origin time to reply
                // with Retry/Initial before we read; avoid silent drops under burst.
                socket.Client.ReceiveBufferSize = 1 << 20;
                socket.Client.SendBufferSize = 1 << 20;
                var gate = new SemaphoreSlim(1, 1);
                var created = new ClientRelay(socket, gate, result.RemoteEndPoint);
                if (relays.TryAdd(key, created))
                {
                    relay = created;
                    _ = RelayBackendToClientAsync(relay);
                }
                else
                {
                    gate.Dispose();
                    socket.Dispose();
                    relay = relays[key];
                }
            }

            if (ShouldDrop())
                continue;

            // Clone: ReceiveAsync may reuse buffers; delay is scheduled off this loop.
            var payload = (byte[])result.Buffer.Clone();
            _ = ForwardAsync(relay.Socket, relay.SendGate, payload, backend, relay, cts.Token);
        }
    }

    private async Task RelayBackendToClientAsync(ClientRelay relay)
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await relay.Socket.ReceiveAsync(cts.Token);
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
            _ = ForwardAsync(listener, listenerSendGate, payload, relay.ClientEndPoint, relay, cts.Token);
        }
    }

    private async Task ForwardAsync(UdpClient socket, SemaphoreSlim sendGate, byte[] payload,
        IPEndPoint destination, ClientRelay relay, CancellationToken cancellationToken)
    {
        try
        {
            // MsQuic on Windows through this NAT completes the handshake with 1-RTT short-header
            // packets (HANDSHAKE_DONE, etc.). Delaying those from the first datagram left both
            // TWP and YARP at sustain 0 (handshake timed out at 10s). Skip delay for a short
            // grace window; loss% still applies to every datagram including the handshake.
            var delay = delayMs > 0 && Environment.TickCount64 >= relay.AllowDelayAfterTick
                ? delayMs
                : 0;
            if (delay > 0)
                await Task.Delay(delay, cancellationToken);
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(payload, destination, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore — peer closed / transient UDP errors under loss
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

        foreach (var kv in relays)
        {
            try
            {
                kv.Value.SendGate.Dispose();
                kv.Value.Socket.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        listenerSendGate.Dispose();

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

    private sealed class ClientRelay
    {
        public ClientRelay(UdpClient socket, SemaphoreSlim sendGate, IPEndPoint clientEndPoint)
        {
            Socket = socket;
            SendGate = sendGate;
            ClientEndPoint = clientEndPoint;
            AllowDelayAfterTick = Environment.TickCount64 + DelayGraceMs;
        }

        public UdpClient Socket { get; }
        public SemaphoreSlim SendGate { get; }
        public IPEndPoint ClientEndPoint { get; }
        public long AllowDelayAfterTick { get; }
    }
}
