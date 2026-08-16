using System.Net;
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

    internal abstract Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ILogger logger);
}