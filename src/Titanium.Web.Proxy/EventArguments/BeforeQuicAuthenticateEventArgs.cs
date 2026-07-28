using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Event arguments raised on a <c>TransparentQuicProxyEndPoint</c> before the QUIC TLS handshake
///     completes, analogous to <see cref="BeforeSslAuthenticateEventArgs" /> for transparent TCP endpoints.
///     <para>
///         Unlike its TCP counterpart, this event does <b>not</b> expose a <c>DecryptSsl</c> property:
///         QUIC always terminates TLS 1.3 at the proxy — there is no pass-through mode. Calling
///         <see cref="Reject" /> is the only way to refuse the connection before the handshake finishes.
///     </para>
///     <para>
///         Thread-safety: each inbound QUIC connection fires a dedicated instance of this event on its own
///         accept task. Handlers that mutate shared state must synchronize themselves.
///     </para>
///     <para>
///         <b>Experimental:</b> HTTP/3 support has not yet completed the full interop/soak/fuzz gate
///         process. Suppress <c>TWP001</c> to opt in.
///     </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("TWP001")]
public class BeforeQuicAuthenticateEventArgs : EventArgs
{
    private UpstreamHttpProtocol upstreamHttpProtocol = UpstreamHttpProtocol.Auto;
    internal readonly CancellationTokenSource TaskCancellationSource;
    internal readonly ProxyServer Server;

    internal BeforeQuicAuthenticateEventArgs(
        ProxyServer server,
        CancellationTokenSource taskCancellationSource,
        string? sniHostName,
        string originalDestinationHost,
        int originalDestinationPort,
        IPEndPoint remoteEndPoint,
        IPEndPoint localEndPoint)
    {
        Server = server;
        TaskCancellationSource = taskCancellationSource;
        SniHostName = sniHostName;
        OriginalDestinationHost = originalDestinationHost;
        OriginalDestinationPort = originalDestinationPort;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
        ForwardHost = originalDestinationHost;
        ForwardPort = originalDestinationPort;
    }

    /// <summary>
    ///     The SNI hostname from the QUIC ClientHello, or <see langword="null" /> if the client did not
    ///     supply SNI. Do not use this as the authoritative original destination — always prefer
    ///     <see cref="OriginalDestinationHost" /> resolved by the configured
    ///     <c>IOriginalDestinationResolver</c>.
    /// </summary>
    public string? SniHostName { get; }

    /// <summary>
    ///     The pre-NAT original destination hostname resolved by the configured
    ///     <c>IOriginalDestinationResolver</c>, or the endpoint's <c>ForwardHost</c> fallback.
    /// </summary>
    public string OriginalDestinationHost { get; }

    /// <summary>
    ///     The pre-NAT original destination port.
    /// </summary>
    public int OriginalDestinationPort { get; }

    /// <summary>
    ///     The remote UDP endpoint of the QUIC client.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>
    ///     The local UDP endpoint the proxy is listening on.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    ///     Per-connection forward target host override. Defaults to <see cref="OriginalDestinationHost" />.
    ///     Mirrors <see cref="BeforeSslAuthenticateEventArgs.ForwardHttpsHostName" />.
    /// </summary>
    public string ForwardHost { get; set; }

    /// <summary>
    ///     Per-connection forward target port override. Defaults to <see cref="OriginalDestinationPort" />.
    ///     Mirrors <see cref="BeforeSslAuthenticateEventArgs.ForwardHttpsPort" />.
    /// </summary>
    public int ForwardPort { get; set; }

    /// <summary>
    ///     Controls which HTTP version the proxy uses on its own outbound connection to the origin for all
    ///     streams on this QUIC connection. Each stream may further override this per-request via
    ///     <see cref="SessionEventArgs.UpstreamHttpProtocol" /> in <c>BeforeRequest</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UpstreamHttpProtocol" /> member.</exception>
    public UpstreamHttpProtocol UpstreamHttpProtocol
    {
        get => upstreamHttpProtocol;
        set => upstreamHttpProtocol = Enum.IsDefined(typeof(UpstreamHttpProtocol), value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "Unknown UpstreamHttpProtocol value.");
    }

    /// <summary>
    ///     Whether the proxy may bridge a mismatch between the inbound H3 client and the outbound protocol
    ///     implied by <see cref="UpstreamHttpProtocol" />. For example, when <c>true</c> and
    ///     <see cref="UpstreamHttpProtocol.Http11" /> is set, inbound H3 streams are bridged to HTTP/1.1
    ///     origin requests. Defaults to <see langword="true" /> for QUIC endpoints (H3 clients reaching
    ///     an H1/H2 origin is the common transparent-proxy case). Mirrors
    ///     <see cref="BeforeSslAuthenticateEventArgs.AllowHttpProtocolTranslation" />.
    /// </summary>
    public bool AllowHttpProtocolTranslation { get; set; } = true;

    /// <summary>
    ///     Per-connection upstream proxy override. <see langword="null" /> uses the server-level
    ///     <see cref="ProxyServer.UpStreamHttpsProxy" /> setting.
    /// </summary>
    public IExternalProxy? CustomUpStreamProxy { get; set; }

    /// <summary>
    ///     Rejects the QUIC connection by cancelling the accept task. The QUIC handshake will not complete
    ///     and the client connection will be closed.
    /// </summary>
    public void Reject() => TaskCancellationSource.Cancel();
}
