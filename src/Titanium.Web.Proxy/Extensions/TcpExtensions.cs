using System;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Extensions;

internal static class TcpExtensions
{
    /// <summary>
    ///     Check if a TcpClient is good to be used.
    ///     This only checks if send is working so local socket is still connected.
    ///     Receive can only be verified by doing a valid read from server without exceptions.
    ///     So in our case we should retry with new connection from pool if first read after getting the connection fails.
    ///     https://msdn.microsoft.com/en-us/library/system.net.sockets.socket.connected(v=vs.110).aspx
    /// </summary>
    /// <param name="socket"></param>
    /// <returns></returns>
    internal static bool IsGoodConnection(this Socket socket)
    {
        if (!socket.Connected) return false;

        // This is how you can determine whether a socket is still connected.
        var blockingState = socket.Blocking;
        try
        {
            var tmp = new byte[1];

            socket.Blocking = false;
            socket.Send(tmp, 0, 0);
            // Connected.
        }
        catch
        {
            // Should we let 10035 == WSAEWOULDBLOCK as valid connection?
            return false;
        }
        finally
        {
            socket.Blocking = blockingState;
        }

        // Send(0) succeeds on a socket whose peer already sent FIN (half-closed), so an origin that
        // idle-closed the keep-alive connection looked healthy and the next request, a POST that cannot
        // be replayed, died with "closed the connection before sending a response". An idle connection
        // that is readable with nothing to read has been closed (FIN) or reset by the peer. Pending data
        // (for example a TLS 1.3 session ticket or close_notify) is left to the stream layer.
        try
        {
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }
}