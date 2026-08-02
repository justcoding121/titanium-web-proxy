using System;
using System.Threading;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     This is used in transparent endpoint before authenticating client.
/// </summary>
public class BeforeSslAuthenticateEventArgs : ProxyEventArgsBase
{
    internal readonly CancellationTokenSource TaskCancellationSource;
    private UpstreamHttpProtocol upstreamHttpProtocol = UpstreamHttpProtocol.Auto;

    internal BeforeSslAuthenticateEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        CancellationTokenSource taskCancellationSource, string sniHostName) : base(server, clientConnection)
    {
        TaskCancellationSource = taskCancellationSource;
        SniHostName = sniHostName;
        ForwardHttpsHostName = sniHostName;
    }

    /// <summary>
    ///     The server name indication hostname if available.
    ///     Otherwise the GenericCertificateName property of TransparentEndPoint.
    /// </summary>
    public string SniHostName { get; }

    /// <summary>
    ///     Should we decrypt the SSL request?
    ///     If true we decrypt with fake certificate.
    ///     If false we relay the connection to the hostname mentioned in SniHostname.
    /// </summary>
    public bool DecryptSsl { get; set; } = true;

    /// <summary>
    ///     Hostname used as the TCP/TLS forward target for this transparent connection.
    ///     Defaults to the SNI hostname from the SSL handshake when available; otherwise the
    ///     <c>GenericCertificateName</c> of the transparent endpoint. Used whether or not
    ///     <see cref="DecryptSsl" /> is true. When decrypting, you may still need to adjust the HTTP
    ///     request identity (for example <c>e.HttpClient.Request.Url</c>) in <c>BeforeRequest</c>.
    /// </summary>
    public string ForwardHttpsHostName { get; set; }

    /// <summary>
    ///     Port used as the TCP/TLS forward target for this transparent connection.
    ///     Defaults to the standard HTTPS port, 443. Used whether or not <see cref="DecryptSsl" /> is true.
    ///     When decrypting, you may still need to adjust the HTTP request identity in <c>BeforeRequest</c>.
    /// </summary>
    public int ForwardHttpsPort { get; set; } = 443;

    /// <summary>
    ///     Controls which HTTP version the proxy uses on its own connection to the origin server for this
    ///     connection, independent of the HTTP version the client itself negotiates with the proxy. Must be
    ///     set during <c>BeforeSslAuthenticate</c> - it is read before the client TLS handshake, and the
    ///     client's own ALPN offer/negotiation cannot change afterward. See <see cref="UpstreamHttpProtocol" />.
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
    ///     Whether the proxy may bridge a mismatch between the client's negotiated HTTP version and the
    ///     origin's HTTP version implied by <see cref="UpstreamHttpProtocol" />. Defaults to <c>false</c>, in
    ///     which case <see cref="UpstreamHttpProtocol.Http11" /> instead simply never offers "h2" to the
    ///     client (so no mismatch, and no translation, is ever needed) and <see cref="UpstreamHttpProtocol.Http2" />
    ///     fails the connection outright if the client does not also support HTTP/2.
    /// </summary>
    public bool AllowHttpProtocolTranslation { get; set; }

    /// <summary>
    ///     Terminate the request abruptly by closing client/server connections.
    /// </summary>
    public void TerminateSession()
    {
        TaskCancellationSource.Cancel();
    }
}