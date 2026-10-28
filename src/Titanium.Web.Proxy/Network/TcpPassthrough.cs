using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network;

/// <summary>Copies bytes between a client socket and an upstream TCP endpoint.</summary>
internal static class TcpPassthrough
{
    public static async Task RelayAsync(Socket client, string host, int port, CancellationToken cancellationToken)
    {
        using var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await upstream.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var forward = PumpAsync(client, upstream, cancellationToken);
        var back = PumpAsync(upstream, client, cancellationToken);
        await Task.WhenAny(forward, back).ConfigureAwait(false);
        try
        {
            client.Close();
            upstream.Close();
        }
        catch (ObjectDisposedException)
        {
            // The peer already closed its side when the first direction finished.
        }

        try
        {
            await Task.WhenAll(forward, back).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing the sockets unblocks both copies. Their fault is the close, not a new failure.
        }
    }

    private static async Task PumpAsync(Socket from, Socket to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await from.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await to.SendAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
