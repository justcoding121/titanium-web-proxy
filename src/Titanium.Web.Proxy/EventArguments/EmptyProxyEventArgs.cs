using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

internal class EmptyProxyEventArgs : ProxyEventArgsBase
{
    internal EmptyProxyEventArgs(ProxyServer server, TcpClientConnection clientConnection) : base(server,
        clientConnection)
    {
    }
}