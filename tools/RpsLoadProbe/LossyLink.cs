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
/// The backend→client receive loop is armed before the first client datagram is forwarded so a
/// fast QUIC Retry/Initial response cannot land on a socket with no reader yet.
/// </summary>
internal sealed class LossyUdpLink : IAsyncDisposable
{
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
            var relay = await GetOrCreateRelayAsync(key, result.RemoteEndPoint);

            if (ShouldDrop())
                continue;

            // Clone: ReceiveAsync may reuse buffers; delay is scheduled off this loop.
            var payload = (byte[])result.Buffer.Clone();
            _ = ForwardAsync(relay.Socket, relay.SendGate, payload, backend, cts.Token);
        }
    }

    private async ValueTask<ClientRelay> GetOrCreateRelayAsync(string key, IPEndPoint clientEp)
    {
        if (relays.TryGetValue(key, out var existing))
            return existing;

        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gate = new SemaphoreSlim(1, 1);
        var receiving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new ClientRelay(socket, gate, clientEp, receiving);
        if (!relays.TryAdd(key, created))
        {
            gate.Dispose();
            socket.Dispose();
            return relays[key];
        }

        _ = RelayBackendToClientAsync(created);
        // Arm ReceiveAsync before the first client→backend forward (QUIC Retry can be immediate).
        await receiving.Task.WaitAsync(cts.Token);
        return created;
    }

    private async Task RelayBackendToClientAsync(ClientRelay relay)
    {
        // Issue the first ReceiveAsync before unblocking the client→backend forward so a
        // QUIC Retry/Initial response cannot race an empty socket recv queue on Windows.
        ValueTask<UdpReceiveResult> receiveTask;
        try
        {
            receiveTask = relay.Socket.ReceiveAsync(cts.Token);
        }
        catch (ObjectDisposedException)
        {
            relay.Receiving.TrySetResult();
            return;
        }

        relay.Receiving.TrySetResult();

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await receiveTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                receiveTask = relay.Socket.ReceiveAsync(cts.Token);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (ShouldDrop())
                continue;

            var payload = (byte[])result.Buffer.Clone();
            _ = ForwardAsync(listener, listenerSendGate, payload, relay.ClientEndPoint, cts.Token);
        }
    }

    private async Task ForwardAsync(UdpClient socket, SemaphoreSlim sendGate, byte[] payload,
        IPEndPoint destination, CancellationToken cancellationToken)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
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
        public ClientRelay(UdpClient socket, SemaphoreSlim sendGate, IPEndPoint clientEndPoint,
            TaskCompletionSource receiving)
        {
            Socket = socket;
            SendGate = sendGate;
            ClientEndPoint = clientEndPoint;
            Receiving = receiving;
        }

        public UdpClient Socket { get; }
        public SemaphoreSlim SendGate { get; }
        public IPEndPoint ClientEndPoint { get; }
        public TaskCompletionSource Receiving { get; }
    }
}
