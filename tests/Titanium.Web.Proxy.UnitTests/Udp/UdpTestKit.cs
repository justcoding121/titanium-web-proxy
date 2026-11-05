using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.UnitTests.Udp;

/// <summary>A clock the test moves by hand. Timers still fire on real time but read this clock, so they never expire a session early.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long milliseconds;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref milliseconds);

    public void Advance(TimeSpan by) => Interlocked.Add(ref milliseconds, (long)by.TotalMilliseconds);
}

/// <summary>UDP backend on loopback that answers every datagram, optionally tagged or padded, and records who sent it.</summary>
internal sealed class UdpBackend : IDisposable
{
    private readonly Socket socket;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;

    public UdpBackend(string tag = "", int padBytes = 0, IPAddress? address = null)
    {
        Tag = tag;
        PadBytes = padBytes;
        address ??= IPAddress.Loopback;
        socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 1 << 20;
        Disable(socket);
        socket.Bind(new IPEndPoint(address, 0));
        loop = Task.Run(RunAsync);
    }

    public string Tag { get; }

    public int PadBytes { get; }

    public int Port => ((IPEndPoint)socket.LocalEndPoint!).Port;

    public ConcurrentQueue<IPEndPoint> Senders { get; } = new();

    /// <summary>Sends a datagram nobody asked for, from the backend's own socket.</summary>
    public void SendTo(byte[] payload, IPEndPoint to) => socket.SendTo(payload, to);

    public int Received => Senders.Count;

    public static void Disable(Socket s)
    {
        if (OperatingSystem.IsWindows())
        {
            s.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
    }

    private async Task RunAsync()
    {
        var buffer = new byte[70_000];
        EndPoint any = new IPEndPoint(
            socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var prefix = Encoding.ASCII.GetBytes(Tag.Length == 0 ? string.Empty : Tag + ":");
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token).ConfigureAwait(false);
                Senders.Enqueue((IPEndPoint)result.RemoteEndPoint);
                var reply = new byte[prefix.Length + result.ReceivedBytes + PadBytes];
                prefix.CopyTo(reply, 0);
                Array.Copy(buffer, 0, reply, prefix.Length, result.ReceivedBytes);
                await socket.SendToAsync(reply, SocketFlags.None, result.RemoteEndPoint, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // The sender went away between receive and reply.
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        socket.Dispose();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Unwound by the dispose.
        }

        cts.Dispose();
    }
}

/// <summary>A UDP client on loopback with a bounded receive.</summary>
internal sealed class UdpTestClient : IDisposable
{
    private readonly Socket socket;

    public UdpTestClient(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        UdpBackend.Disable(socket);
        socket.Bind(new IPEndPoint(address, 0));
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public void Send(byte[] payload, IPEndPoint to) => socket.SendTo(payload, to);

    public void Send(string text, IPEndPoint to) => Send(Encoding.ASCII.GetBytes(text), to);

    /// <summary>The next datagram, or null when none arrives within the timeout.</summary>
    public async Task<byte[]?> ReceiveAsync(int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[70_000];
        EndPoint any = new IPEndPoint(
            socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token).ConfigureAwait(false);
            return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<string?> ReceiveTextAsync(int timeoutMs = 2000)
    {
        var data = await ReceiveAsync(timeoutMs).ConfigureAwait(false);
        return data is null ? null : Encoding.ASCII.GetString(data);
    }

    public void Dispose() => socket.Dispose();
}

internal static class UdpWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
