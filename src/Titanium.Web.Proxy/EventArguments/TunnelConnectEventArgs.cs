using System;
using System.Threading;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     A class that wraps the state when a tunnel connect event happen for Explicit endpoints.
/// </summary>
public class TunnelConnectSessionEventArgs : SessionEventArgsBase
{
    private bool? isHttpsConnect;
    private UpstreamHttpProtocol upstreamHttpProtocol = UpstreamHttpProtocol.Auto;

    internal TunnelConnectSessionEventArgs(ProxyServer server, ProxyEndPoint endPoint, ConnectRequest connectRequest,
        HttpClientStream clientStream, CancellationTokenSource cancellationTokenSource)
        : base(server, endPoint, clientStream, connectRequest, connectRequest, cancellationTokenSource)
    {
    }

    /// <summary>
    ///     Should we decrypt the Ssl or relay it to server?
    ///     Default is true.
    /// </summary>
    public bool DecryptSsl { get; set; } = true;

    /// <summary>
    ///     When set to true it denies the connect request with a Forbidden status.
    /// </summary>
    public bool DenyConnect { get; set; }

    /// <summary>
    ///     When <see langword="true" />, the proxy establishes the upstream TCP connection
    ///     (and upstream-proxy CONNECT when configured) <b>before</b> writing HTTP 200 to the client.
    ///     On failure, <see cref="ExplicitProxyEndPoint.BeforeTunnelConnectFailure" /> can supply a
    ///     custom HTTP error response (the client never receives 200 / never starts TLS).
    ///     Default is <see langword="false" /> — no preconnect and no added latency.
    ///     Set this during <see cref="ExplicitProxyEndPoint.BeforeTunnelConnectRequest" />.
    /// </summary>
    public bool EstablishServerConnectionBeforeResponse { get; set; }

    /// <summary>
    ///     Controls which HTTP version the proxy uses on its own connection to the origin server for this
    ///     tunnel, independent of the HTTP version the client itself negotiates with the proxy. Must be set
    ///     during <c>BeforeTunnelConnectRequest</c> - it is read before the client TLS handshake, and the
    ///     client's own ALPN offer/negotiation cannot change afterward. See <see cref="UpstreamHttpProtocol" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="UpstreamHttpProtocol" /> member.</exception>
    public UpstreamHttpProtocol UpstreamHttpProtocol
    {
        get => upstreamHttpProtocol;
        set => upstreamHttpProtocol = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "Unknown UpstreamHttpProtocol value.");
    }

    /// <summary>
    ///     Whether the proxy may bridge a mismatch between the client's negotiated HTTP version and the
    ///     origin's HTTP version implied by <see cref="UpstreamHttpProtocol" />. Defaults to <c>false</c>, in
    ///     which case <see cref="UpstreamHttpProtocol.Http11" /> instead simply never offers "h2" to the
    ///     client (so no mismatch, and no translation, is ever needed) and <see cref="UpstreamHttpProtocol.Http2" />
    ///     fails the connection outright if the client does not also support HTTP/2.
    /// </summary>
    public bool AllowHttpProtocolTranslation { get; set; }

    /// <summary>
    ///     Timing of the client-facing (browser-to-proxy) TLS handshake performed while decrypting this
    ///     tunnel, populated only when <see cref="ProxyServer.EnableRequestTimingCapture" /> is enabled and
    ///     <see cref="DecryptSsl" /> is <see langword="true" />; <see langword="null" /> otherwise (including
    ///     for a plain, non-HTTPS CONNECT tunnel that is never TLS-decrypted at all).
    /// </summary>
    public ClientTlsTiming? ClientTlsTiming { get; internal set; }

    /// <summary>
    ///     CONNECT-phase milestones (certificate readiness, origin capability resolution, HTTP/2 probe,
    ///     browser TLS) populated only when <see cref="ProxyServer.EnableRequestTimingCapture" /> is
    ///     enabled and <see cref="DecryptSsl" /> is <see langword="true" />; otherwise
    ///     <see langword="null" />.
    /// </summary>
    public TunnelConnectTiming? ConnectTiming { get; internal set; }

    /// <summary>
    ///     Is this a connect request to secure HTTP server? Or is it to some other protocol.
    /// </summary>
    public bool IsHttpsConnect
    {
        get => isHttpsConnect ??
               throw new InvalidOperationException("The value of this property is known in the BeforeTunnelConnectResponse event");

        internal set => isHttpsConnect = value;
    }

    /// <summary>
    ///     Fired when decrypted data is sent within this session to server/client.
    /// </summary>
    public event EventHandler<DataEventArgs>? DecryptedDataSent;

    /// <summary>
    ///     Fired when decrypted data is received within this session from client/server.
    /// </summary>
    public event EventHandler<DataEventArgs>? DecryptedDataReceived;

    internal void OnDecryptedDataSent(byte[] buffer, int offset, int count)
    {
        try
        {
            DecryptedDataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    internal void OnDecryptedDataReceived(byte[] buffer, int offset, int count)
    {
        try
        {
            DecryptedDataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

}