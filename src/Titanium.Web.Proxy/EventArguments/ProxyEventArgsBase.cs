using System;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     The base event arguments
/// </summary>
/// <seealso cref="System.EventArgs" />
public abstract class ProxyEventArgsBase : EventArgs // NOSONAR S3376 -- Public API name is retained for compatibility.
{
    private readonly TcpClientConnection clientConnection;
    internal readonly ProxyServer Server;

    private protected ProxyEventArgsBase(ProxyServer server, TcpClientConnection clientConnection)
    {
        this.clientConnection = clientConnection;
        Server = server;
    }

    public object? ClientUserData
    {
        get => clientConnection.ClientUserData;
        set => clientConnection.ClientUserData = value;
    }
}