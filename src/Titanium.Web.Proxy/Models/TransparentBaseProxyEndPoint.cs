using System.Net;
using System.Net.Security;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Models;

public abstract class TransparentBaseProxyEndPoint : ProxyEndPoint
{
    protected TransparentBaseProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl) : base(ipAddress, port,
        decryptSsl)
    {
    }

    /// <summary>
    ///     The hostname of the generic certificate to negotiate SSL.
    ///     This will be only used when Sever Name Indication (SNI) is not supported by client,
    ///     or when it does not indicate any host name.
    /// </summary>
    public abstract string GenericCertificateName { get; set; }

    /// <summary>
    ///     Optional fixed upstream server to forward all traffic on this endpoint to.
    ///     Only the TCP connection target is changed; the original host is still used
    ///     for TLS SNI/certificate validation and the HTTP Host header.
    /// </summary>
    public string? ForwardHost { get; set; }

    /// <summary>
    ///     Optional fixed upstream port. When null the original request port is used.
    /// </summary>
    public int? ForwardPort { get; set; }

    /// <summary>
    ///     When <see langword="true" /> together with <see cref="ProxyEndPoint.DecryptSsl" />, the proxy
    ///     terminates client TLS and opens a <b>cleartext</b> upstream TCP connection to
    ///     <see cref="ForwardHost" />/<see cref="ForwardPort" /> (classic TLS-terminating reverse proxy).
    ///     Defaults to <see langword="false" /> (re-encrypt to the origin when the client spoke HTTPS).
    /// </summary>
    public bool ForwardCleartext { get; set; }

    /// <summary>
    ///     Cached H3→H2 origin pool identity for the interception-off fast path when <c>:authority</c>
    ///     is stable (typical reverse probe). Avoids rebuilding the pool-key string every request.
    /// </summary>
    internal ByteString CachedH2OriginAuthority;
    internal string? CachedH2OriginHost;
    internal int CachedH2OriginPort;
    internal string? CachedH2OriginPoolKey;

    /// <summary>
    ///     Cached <c>Host</c> header derived from a stable client <c>:authority</c> (reverse probes).
    ///     Avoids <c>Authority.GetString()</c> per H2→H1 stream.
    /// </summary>
    internal string? CachedForwardHttpHost;
    internal ByteString CachedHostAuthority;

    /// <summary>
    ///     Cached TCP pool key for fixed-forward H1 origin (H2→H1 bridge). Avoids rebuilding the
    ///     pool-key string on every multiplexed stream.
    /// </summary>
    internal string? CachedHttp11PoolKey;
    internal bool CachedHttp11PoolIsHttps;

    /// <summary>
    ///     Prebuilt server TLS options for fixed <see cref="ProxyEndPoint.GenericCertificate" /> reverse
    ///     terminate (compare-tls-cost / sticky leaf). Avoids allocating options per new connection.
    /// </summary>
    internal SslServerAuthenticationOptions? CachedServerAuthOptions;

    internal abstract bool HasBeforeSslAuthenticateHandlers { get; }

    internal abstract Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ILogger logger);

    internal abstract Task InvokeBeforeHttpAuthenticate(ProxyServer proxyServer,
        BeforeHttpAuthenticateEventArgs args, ILogger logger);
}