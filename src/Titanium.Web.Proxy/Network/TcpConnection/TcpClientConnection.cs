using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     An object that holds TcpConnection to a particular server and port
/// </summary>
internal class TcpClientConnection : IDisposable
{
    private readonly Socket tcpClientSocket;

    private bool disposed;

    private int? processId;

    internal TcpClientConnection(ProxyServer proxyServer, Socket tcpClientSocket)
    {
        this.tcpClientSocket = tcpClientSocket;
        ProxyServer = proxyServer;
        ProxyServer.UpdateClientConnectionCount(true);
    }

    public object? ClientUserData { get; set; }

    private ProxyServer ProxyServer { get; }

    public Guid Id { get; } = Guid.NewGuid();

    public EndPoint LocalEndPoint => tcpClientSocket.LocalEndPoint
                                     ?? throw new InvalidOperationException("Client socket has no local endpoint.");

    public EndPoint RemoteEndPoint => tcpClientSocket.RemoteEndPoint
                                      ?? throw new InvalidOperationException("Client socket has no remote endpoint.");

    internal SslProtocols SslProtocol { get; set; }

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        // No finalizer: sockets already have safe-handle finalization, and scheduling
        // Task.Run / logging from a finalizer thread is unsafe.
        Task.Run(async () =>
        {
            // delay calling tcp connection close()
            // so that client have enough time to call close first.
            // This way we can push tcp Time_Wait to client side when possible.
            await Task.Delay(1000);
            ProxyServer.UpdateClientConnectionCount(false);

            try
            {
                tcpClientSocket.Close();
            }
            catch (Exception ex)
            {
                Logging.ProxyDiagnostics.ReportBenign(ProxyServer.Logger,
                    "Failed to close a client socket during disposal.", ex);
            }
        });
    }

    public Stream GetStream()
    {
        return new NetworkStream(tcpClientSocket, true);
    }

    public int GetProcessId(ProxyEndPoint endPoint)
    {
        if (processId.HasValue) return processId.Value;

        if (RunTime.IsWindows)
        {
            var remoteEndPoint = (IPEndPoint)RemoteEndPoint;

            // If client is localhost get the process id
            if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                processId = TcpHelper.GetProcessIdByLocalPort(endPoint.IpAddress.AddressFamily, remoteEndPoint.Port);
            else
                // can't access process Id of remote request from remote machine
                processId = -1;

            return processId.Value;
        }

        throw new PlatformNotSupportedException();
    }
}