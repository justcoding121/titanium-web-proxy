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
    ///     We need to know the server hostname we are forwarding the request to.
    ///     By default its the SNI hostname indicated in SSL handshake, when SNI is available.
    ///     When SNI is not available, it will use the GenericCertificateName of TransparentEndPoint.
    ///     This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
    ///     When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting
    ///     e.HttpClient.Request.Url inside BeforeRequest event handler.
    /// </summary>
    public string ForwardHttpsHostName { get; set; }

    /// <summary>
    ///     We need to know the server port we are forwarding the request to.
    ///     By default its the standard https port, 443.
    ///     This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
    ///     When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting
    ///     e.HttpClient.Request.Url inside BeforeRequest event handler.
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