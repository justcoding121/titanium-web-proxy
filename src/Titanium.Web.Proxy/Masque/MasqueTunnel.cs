using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Masque;

/// <summary>A connected UDP socket and the one-shot release for one CONNECT-UDP tunnel.</summary>
internal sealed class MasqueTunnelOpen
{
    private readonly Socket socket;
    private readonly Action release;
    private int released;

    internal MasqueTunnelOpen(Socket socket, Action release)
    {
        this.socket = socket;
        this.release = release;
    }

    internal Socket Socket => socket;

    internal void Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        try
        {
            socket.Dispose();
        }
        catch (Exception)
        {
            // The receive loop unblocks on disposal.
        }

        release();
    }
}

/// <summary>Opens and relays one CONNECT-UDP tunnel. The HTTP listener owns the response.</summary>
internal static class MasqueTunnel
{
    /// <summary>Returns a rejection status. Zero means the returned <c>Open</c> owns the socket.</summary>
    public static async Task<(int Status, string? Body, MasqueTunnelOpen? Open)> OpenAsync(
        MasqueProxyEndPoint endPoint, string host, int port, ILogger logger, CancellationToken cancellationToken)
    {
        var resolved = await ResolveHostAsync(endPoint, host, logger, cancellationToken).ConfigureAwait(false);

        var allowed = MasqueTargetPolicy.SelectAll(port, endPoint.Options.AllowedPorts,
            endPoint.Options.BlockPrivateTargets, resolved, out var rejection);
        if (allowed.Count == 0)
        {
            var status = rejection == "port" || rejection == "blocked" ? 403 : 502;
            return (status, "connect-udp target was rejected", null);
        }

        var (udp, connectError) = await ConnectFirstAsync(endPoint, allowed, port, cancellationToken)
            .ConfigureAwait(false);

        if (udp is null)
        {
            if (connectError is not null)
            {
                ProxyDiagnostics.ReportBenign(logger, "MASQUE UDP connect failed", connectError);
            }

            return (502, "connect-udp target was rejected", null);
        }

        return (0, null, new MasqueTunnelOpen(udp, static () => { }));
    }

    public static async Task RelayAsync(MasqueProxyEndPoint endPoint, ChannelReader<ReadOnlyMemory<byte>> inbound,
        Stream body, Socket udp, ILogger logger, CancellationToken cancellationToken)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = new CapsuleDatagramChannel(inbound, body);
        var budget = new DatagramBudget(endPoint.Options.MaxDatagramsPerSecond, endPoint.Options.MaxBytesPerSecond);
        var idle = new IdleClock();
        var idleMs = (long)endPoint.Options.IdleTimeout.TotalMilliseconds;
        var toTarget = StartClientToTarget(channel, payload =>
        {
            if (!budget.TryTake(payload.Length))
            {
                endPoint.CountDrop();
                return;
            }

            idle.Touch();
            try
            {
                udp.Send(payload);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                endPoint.CountDrop();
                return;
            }

            endPoint.CountDatagram(fromClient: true);
        }, relay.Token);
        var toClient = ReceiveTargetAsync(endPoint, channel, udp, budget, idle, relay.Token);
        var writer = StartWriter(channel, relay.Token);
        var idleWatch = WatchIdleAsync(endPoint, idle, idleMs, relay);
        await Task.WhenAny(toTarget, toClient, writer, idleWatch).ConfigureAwait(false);
        await relay.CancelAsync().ConfigureAwait(false);
        try
        {
            udp.Close();
        }
        catch (Exception)
        {
            // Closing the socket unblocks the receive loop.
        }

        await Task.WhenAll(
            ObserveRelayAsync(toTarget, logger),
            ObserveRelayAsync(toClient, logger),
            ObserveRelayAsync(writer, logger)).ConfigureAwait(false);
    }

    private static async Task<IPAddress[]> ResolveHostAsync(MasqueProxyEndPoint endPoint, string host, ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                return [literal];
            }

            if (endPoint.Options.DnsResolver is { } resolver)
            {
                return await resolver(host, cancellationToken).ConfigureAwait(false);
            }

            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(logger, "MASQUE target DNS failed", ex);
            return [];
        }
    }

    private static async Task<(Socket? Socket, Exception? Error)> ConnectFirstAsync(MasqueProxyEndPoint endPoint,
        IReadOnlyList<IPAddress> allowed, int port, CancellationToken cancellationToken)
    {
        Socket? udp = null;
        Exception? connectError = null;
        foreach (var address in allowed)
        {
            Socket? candidate = null;
            try
            {
                candidate = endPoint.Options.SocketFactory?.Invoke(address.AddressFamily)
                            ?? new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                DisableWindowsUdpReset(candidate);
                await candidate.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                udp = candidate;
                break;
            }
            catch (Exception ex)
            {
                connectError = ex;
                candidate?.Dispose();
            }
        }

        return (udp, connectError);
    }

    private static void DisableWindowsUdpReset(Socket candidate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            candidate.IOControl(-1744830452, new byte[4], null);
        }
        catch (Exception)
        {
            // The receive loop still tolerates ConnectionReset.
        }
    }

    private static Task StartClientToTarget(CapsuleDatagramChannel channel, DatagramSend send, CancellationToken cancellationToken) =>
        channel.RunClientToTargetAsync(send, cancellationToken).AsTask();

    private static Task StartWriter(CapsuleDatagramChannel channel, CancellationToken cancellationToken) =>
        channel.WriteQueuedAsync(cancellationToken).AsTask();

    private static async Task ObserveRelayAsync(Task task, ILogger logger)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The tunnel is closing.
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(logger, "MASQUE relay task faulted", ex);
        }
    }

    private static async Task ReceiveTargetAsync(MasqueProxyEndPoint endPoint, CapsuleDatagramChannel channel,
        Socket udp, DatagramBudget budget, IdleClock idle, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CapsuleReader.DefaultMaxDatagramBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await udp.ReceiveAsync(buffer.AsMemory(0, CapsuleReader.DefaultMaxDatagramBytes),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    endPoint.CountDrop();
                    continue;
                }

                if (!budget.TryTake(read))
                {
                    endPoint.CountDrop();
                    continue;
                }

                idle.Touch();
                if (!channel.TryQueueToClient(buffer.AsSpan(0, read)))
                {
                    endPoint.CountDrop();
                    continue;
                }

                endPoint.CountDatagram(fromClient: false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The tunnel is closing.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WatchIdleAsync(MasqueProxyEndPoint endPoint, IdleClock idle, long idleMs,
        CancellationTokenSource relay)
    {
        while (!relay.IsCancellationRequested)
        {
            await Task.Delay(500, relay.Token).ConfigureAwait(false);
            if (Environment.TickCount64 - idle.Last > idleMs)
            {
                endPoint.CountIdleClose();
                await relay.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }
}
