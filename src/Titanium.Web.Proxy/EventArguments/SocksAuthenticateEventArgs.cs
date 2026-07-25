using System.Net;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Context for SOCKS5 username/password authentication on a
///     <see cref="SocksProxyEndPoint" />.
/// </summary>
public class SocksAuthenticateEventArgs : ProxyEventArgsBase
{
    internal SocksAuthenticateEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        SocksProxyEndPoint endPoint, string userName, string password)
        : base(server, clientConnection)
    {
        ProxyEndPoint = endPoint;
        ClientRemoteEndPoint = (IPEndPoint)clientConnection.RemoteEndPoint;
        UserName = userName;
        Password = password;
    }

    /// <summary>
    ///     The SOCKS endpoint that accepted this client.
    /// </summary>
    public SocksProxyEndPoint ProxyEndPoint { get; }

    /// <summary>
    ///     Remote endpoint of the connecting SOCKS client.
    /// </summary>
    public IPEndPoint ClientRemoteEndPoint { get; }

    /// <summary>
    ///     Username supplied by the client (RFC 1929).
    /// </summary>
    public string UserName { get; }

    /// <summary>
    ///     Password supplied by the client (RFC 1929).
    /// </summary>
    public string Password { get; }
}
