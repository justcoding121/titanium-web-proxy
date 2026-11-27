using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic.UdpOffload;

namespace Titanium.Quic;

/// <summary>One received UDP datagram and the remote endpoint the socket reported.</summary>
internal readonly struct UdpReceived
{
    internal UdpReceived(int count, IPEndPoint remote)
    {
        Count = count;
        Remote = remote;
    }

    internal int Count { get; }

    internal IPEndPoint Remote { get; }
}

/// <summary>
/// Packet socket used by the driver. A batch of datagrams uses <see cref="UdpOffloadBatch"/> when its probe accepts the call, and otherwise one send at a time.
/// Received ECN and a portable path-MTU socket API are still gaps.
/// </summary>
internal interface IUdpPacketTransport : IDisposable
{
    /// <summary>The bound local endpoint. The port is chosen by the operating system.</summary>
    IPEndPoint LocalEndpoint { get; }

    /// <summary>Largest UDP payload this socket will send.</summary>
    int MaximumDatagramSize { get; }

    /// <summary>True when the socket already has a datagram to read.</summary>
    bool DatagramPending { get; }

    /// <summary>Sends one datagram.</summary>
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken cancellationToken);

    /// <summary>Sends the datagrams. A batch syscall is used when the offload probe has accepted it.</summary>
    ValueTask<int> SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, CancellationToken cancellationToken);

    /// <summary>Posts one receive. Callers overlap several of these.</summary>
    ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>UDP socket. Addresses are pooled. Each receive is one <see cref="Socket.ReceiveFromAsync(Memory{byte}, SocketFlags, SocketAddress, CancellationToken)"/>.</summary>
internal sealed class UdpSocketTransport : IUdpPacketTransport
{
    internal const int RecommendedReceiveOverlap = 4;

    private const int IPv4Payload = 65507;

    private const int IPv6Payload = 65527;

    private const int LinuxReusePort = 15;

    private readonly Socket _socket;

    private readonly Stack<SocketAddress> _addresses = new();

    private readonly object _gate = new();

    private readonly SemaphoreSlim _send = new(1, 1);

    internal UdpSocketTransport(IPEndPoint? local = null)
    {
        local ??= new IPEndPoint(IPAddress.Loopback, 0);
        _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        TryReusePort(_socket);
        QuicDatagramSocket.TryEnableDontFragment(_socket);
        _socket.Bind(local);
        LocalEndpoint = (IPEndPoint)_socket.LocalEndPoint!;
        MaximumDatagramSize = local.AddressFamily == AddressFamily.InterNetworkV6 ? IPv6Payload : IPv4Payload;
    }

    public IPEndPoint LocalEndpoint { get; }

    public int MaximumDatagramSize { get; }

    public bool DatagramPending => _socket.Available > 0;

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (datagram.Length > MaximumDatagramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(datagram));
        }

        // A pooled address can be issued again before the send uses it.
        var address = new SocketAddress(destination.AddressFamily);
        Write(address, destination);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _socket.SendToAsync(datagram, SocketFlags.None, address, cancellationToken).ConfigureAwait(false);
        }
        catch (NullReferenceException)
        {
            // A close that overlaps this send clears the socket handle inside the operation.
            return 0;
        }
        finally
        {
            _send.Release();
        }
    }

    public async ValueTask<int> SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (datagrams.Length > 1 && UdpOffloadBatch.TrySend(_socket, datagrams, destination, out int datagramsSent, out int bytes))
        {
            for (int i = datagramsSent; i < datagrams.Length; i++)
            {
                bytes += await SendAsync(datagrams.Span[i], destination, cancellationToken).ConfigureAwait(false);
            }

            return bytes;
        }

        int sent = 0;
        for (int i = 0; i < datagrams.Length; i++)
        {
            sent += await SendAsync(datagrams.Span[i], destination, cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    public async ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        SocketAddress address = RentAddress();
        try
        {
            int count = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, address, cancellationToken).ConfigureAwait(false);
            return new UdpReceived(count, ToEndpoint(address));
        }
        finally
        {
            ReturnAddress(address);
        }
    }

    /// <summary>Sets an outgoing ECN codepoint when the socket accepts it. Received ECN is not visible through this receive API.</summary>
    internal bool TrySetEcn(byte codepoint)
    {
        if (codepoint > 3)
        {
            return false;
        }

        try
        {
            SocketOptionLevel level = _socket.AddressFamily == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
            _socket.SetSocketOption(level, SocketOptionName.TypeOfService, codepoint);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose() => _socket.Dispose();

    private SocketAddress RentAddress()
    {
        lock (_gate)
        {
            if (_addresses.Count > 0)
            {
                SocketAddress pooled = _addresses.Pop();
                pooled.Size = pooled.Buffer.Length;
                return pooled;
            }
        }

        return new SocketAddress(_socket.AddressFamily);
    }

    private void ReturnAddress(SocketAddress address)
    {
        lock (_gate)
        {
            if (_addresses.Count < RecommendedReceiveOverlap)
            {
                _addresses.Push(address);
            }
        }
    }

    private static void TryReusePort(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)LinuxReusePort, 1);
        }
        catch (SocketException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void Write(SocketAddress address, IPEndPoint endpoint)
    {
        Span<byte> raw = address.Buffer.Span;
        int port = endpoint.Port;
        raw[2] = (byte)(port >> 8);
        raw[3] = (byte)port;
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            endpoint.Address.TryWriteBytes(raw.Slice(4, 4), out _);
            address.Size = 16;
            return;
        }

        endpoint.Address.TryWriteBytes(raw.Slice(8, 16), out _);
        address.Size = 28;
    }

    private static IPEndPoint ToEndpoint(SocketAddress address)
    {
        ReadOnlySpan<byte> raw = address.Buffer.Span;
        int port = (raw[2] << 8) | raw[3];
        if (address.Family == AddressFamily.InterNetwork)
        {
            return new IPEndPoint(new IPAddress(raw.Slice(4, 4)), port);
        }

        return new IPEndPoint(new IPAddress(raw.Slice(8, 16)), port);
    }
}
