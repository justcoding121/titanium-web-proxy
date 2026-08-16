using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A proxy end point client is not aware of.
///     Useful when requests are redirected to this proxy end point through port forwarding via router.
/// </summary>
[DebuggerDisplay("Transparent: {IpAddress}:{Port}")]
public class TransparentProxyEndPoint : TransparentBaseProxyEndPoint
{
    /// <summary>
    ///     Initialize a new instance.
    /// </summary>
    /// <param name="ipAddress">Listening Ip address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
        GenericCertificateName = "localhost";
    }

    /// <summary>
    ///     Name of the Certificate need to be sent (same as the hostname we want to proxy).
    ///     This is valid only when UseServerNameIndication is set to false.
    /// </summary>
    public override string GenericCertificateName { get; set; }

    /// <summary>
    ///     Before Ssl authentication this event is fired.
    /// </summary>
    public event AsyncEventHandler<BeforeSslAuthenticateEventArgs>? BeforeSslAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    /// <summary>
    ///     Before handling a cleartext (non-TLS) client session — including prior-knowledge HTTP/2 (h2c) —
    ///     this event is fired so upstream protocol policy can be set without a ClientHello.
    /// </summary>
    public event AsyncEventHandler<BeforeHttpAuthenticateEventArgs>? BeforeHttpAuthenticate; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    internal override async Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ILogger logger)
    {
        if (BeforeSslAuthenticate != null)
            await BeforeSslAuthenticate.InvokeAsync(proxyServer, connectArgs, logger);
    }

    internal override async Task InvokeBeforeHttpAuthenticate(ProxyServer proxyServer,
        BeforeHttpAuthenticateEventArgs args, ILogger logger)
    {
        if (BeforeHttpAuthenticate != null)
            await BeforeHttpAuthenticate.InvokeAsync(proxyServer, args, logger);
    }
}