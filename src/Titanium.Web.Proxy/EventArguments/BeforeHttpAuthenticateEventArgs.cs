using System;
using System.Threading;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Fired on a transparent reverse endpoint before handling a cleartext (non-TLS) client session,
///     including prior-knowledge HTTP/2 (h2c). Mirrors the policy knobs on
///     <see cref="BeforeSslAuthenticateEventArgs" /> without a TLS handshake.
/// </summary>
public class BeforeHttpAuthenticateEventArgs : ProxyEventArgsBase
{
    internal readonly CancellationTokenSource TaskCancellationSource;
    private UpstreamHttpProtocol upstreamHttpProtocol = UpstreamHttpProtocol.Auto;

    internal BeforeHttpAuthenticateEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        CancellationTokenSource taskCancellationSource, string forwardHostName, int forwardPort)
        : base(server, clientConnection)
    {
        TaskCancellationSource = taskCancellationSource;
        ForwardHostName = forwardHostName;
        ForwardPort = forwardPort;
    }

    /// <summary>
    ///     Hostname used as the TCP forward target for this cleartext connection.
    /// </summary>
    public string ForwardHostName { get; set; }

    /// <summary>
    ///     Port used as the TCP forward target for this cleartext connection.
    /// </summary>
    public int ForwardPort { get; set; }

    /// <summary>
    ///     Controls which HTTP version the proxy uses on its connection to the origin for this session.
    ///     See <see cref="UpstreamHttpProtocol" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined member.</exception>
    public UpstreamHttpProtocol UpstreamHttpProtocol
    {
        get => upstreamHttpProtocol;
        set => upstreamHttpProtocol = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "Unknown UpstreamHttpProtocol value.");
    }

    /// <summary>
    ///     Whether the proxy may bridge a mismatch between the client's HTTP version and the origin
    ///     version implied by <see cref="UpstreamHttpProtocol" />.
    /// </summary>
    public bool AllowHttpProtocolTranslation { get; set; }

    /// <summary>
    ///     Terminate the session by closing client/server connections.
    /// </summary>
    public void TerminateSession() => TaskCancellationSource.Cancel();
}
