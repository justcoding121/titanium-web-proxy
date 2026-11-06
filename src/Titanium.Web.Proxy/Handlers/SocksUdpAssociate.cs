using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     SOCKS5 UDP ASSOCIATE (RFC 1928). The TCP connection is the control channel.
    ///     Closing it tears the relay down. This is not <c>UdpRelayListener</c>.
    /// </summary>
    private async Task RunSocks5UdpAssociateAsync(System.IO.Stream control, TcpClientConnection client,
        IPAddress? requestedClient, CancellationToken cancellationToken)
    {
        var local = client.LocalEndPoint as IPEndPoint;
        var bindAddress = local?.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;
        if (local is not null && !IPAddress.IsLoopback(local.Address) &&
            !local.Address.Equals(IPAddress.Any) && !local.Address.Equals(IPAddress.IPv6Any))
        {
            bindAddress = local.Address;
        }

        using var relay = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        relay.Bind(new IPEndPoint(bindAddress, 0));
        var bound = (IPEndPoint)relay.LocalEndPoint!;
        var reply = new byte[4 + bound.Address.GetAddressBytes().Length + 2];
        reply[0] = 5;
        reply[3] = bound.Address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
        bound.Address.GetAddressBytes().CopyTo(reply, 4);
        reply[^2] = (byte)(bound.Port >> 8);
        reply[^1] = (byte)bound.Port;
        await control.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var expected = requestedClient ?? (client.RemoteEndPoint as IPEndPoint)?.Address;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = WatchSocksControlAsync(control, relay, stop);
        try
        {
            await RelaySocksUdpAsync(relay, expected, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The control connection closed or the relay socket was torn down.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                relay.Close();
            }
            catch (Exception)
            {
                // Closing the socket unblocks the receive loop.
            }

            try
            {
                await watch.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The control read ends when the client disconnects.
            }
        }
    }

    private static async Task WatchSocksControlAsync(System.IO.Stream control, Socket relay, CancellationTokenSource stop)
    {
        var one = new byte[1];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var read = await control.ReadAsync(one, stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // The control connection is gone.
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            relay.Close();
        }
        catch (Exception)
        {
            // The relay loop observes the closed socket.
        }
    }

    private static async Task RelaySocksUdpAsync(Socket relay, IPAddress? expectedClient, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65535);
        var outbound = ArrayPool<byte>.Shared.Rent(65535);
        EndPoint client = null!;
        var haveClient = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EndPoint remote = relay.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any, 0);
                SocketReceiveFromResult received;
                try
                {
                    received = await relay.ReceiveFromAsync(buffer.AsMemory(0, 65535), SocketFlags.None, remote,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                     or SocketError.MessageSize)
                {
                    continue;
                }
                if (received.RemoteEndPoint is not IPEndPoint from)
                {
                    continue;
                }

                if (!haveClient)
                {
                    if (expectedClient is not null && !from.Address.Equals(expectedClient))
                    {
                        continue;
                    }

                    client = from;
                    haveClient = true;
                }

                if (from.Equals(client))
                {
                    if (!TryParseSocksUdp(buffer.AsSpan(0, received.ReceivedBytes), out var host, out var port,
                            out var headerLength))
                    {
                        continue;
                    }

                    var payloadLength = received.ReceivedBytes - headerLength;
                    if (!IPAddress.TryParse(host, out var targetAddress))
                    {
                        var resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                        if (resolved.Length == 0)
                        {
                            continue;
                        }

                        targetAddress = resolved[0];
                    }

                    await relay.SendToAsync(buffer.AsMemory(headerLength, payloadLength), SocketFlags.None,
                        new IPEndPoint(targetAddress, port), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var wrapped = WriteSocksUdp(outbound, from, buffer.AsSpan(0, received.ReceivedBytes));
                if (wrapped > 0 && haveClient)
                {
                    await relay.SendToAsync(outbound.AsMemory(0, wrapped), SocketFlags.None, client, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(outbound);
        }
    }

    private static bool TryParseSocksUdp(ReadOnlySpan<byte> packet, out string host, out int port, out int headerLength)
    {
        host = string.Empty;
        port = 0;
        headerLength = 0;
        if (packet.Length < 4 || packet[2] != 0)
        {
            return false;
        }

        switch (packet[3])
        {
            case 1 when packet.Length >= 10:
                host = new IPAddress(packet.Slice(4, 4)).ToString();
                port = (packet[8] << 8) + packet[9];
                headerLength = 10;
                return port > 0;
            case 4 when packet.Length >= 22:
                host = new IPAddress(packet.Slice(4, 16)).ToString();
                port = (packet[20] << 8) + packet[21];
                headerLength = 22;
                return port > 0;
            case 3 when packet.Length >= 5:
                var nameLength = packet[4];
                if (packet.Length < 5 + nameLength + 2 || nameLength == 0)
                {
                    return false;
                }

                host = Encoding.ASCII.GetString(packet.Slice(5, nameLength));
                port = (packet[5 + nameLength] << 8) + packet[6 + nameLength];
                headerLength = 7 + nameLength;
                return port > 0;
            default:
                return false;
        }
    }

    private static int WriteSocksUdp(Span<byte> destination, IPEndPoint from, ReadOnlySpan<byte> payload)
    {
        var address = from.Address.GetAddressBytes();
        var header = 4 + address.Length + 2;
        if (destination.Length < header + payload.Length)
        {
            return 0;
        }

        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = from.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
        address.CopyTo(destination[4..]);
        destination[4 + address.Length] = (byte)(from.Port >> 8);
        destination[5 + address.Length] = (byte)from.Port;
        payload.CopyTo(destination[header..]);
        return header + payload.Length;
    }
}
