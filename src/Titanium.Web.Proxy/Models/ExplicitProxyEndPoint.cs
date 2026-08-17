using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A proxy endpoint that the client is aware of.
///     So client application know that it is communicating with a proxy server.
/// </summary>
[DebuggerDisplay("Explicit: {IpAddress}:{Port}")]
public class ExplicitProxyEndPoint : ProxyEndPoint
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="ipAddress">Listening IP address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public ExplicitProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
    }

    internal bool IsSystemHttpProxy { get; set; }

    internal bool IsSystemHttpsProxy { get; set; }

    /// <summary>
    ///     Intercept tunnel connect request.
    ///     Valid only for explicit endpoints.
    ///     Set the <see cref="TunnelConnectSessionEventArgs.DecryptSsl" /> property to false if this HTTP connect request
    ///     shouldn't be decrypted and instead be relayed.
    /// </summary>
    public event AsyncEventHandler<TunnelConnectSessionEventArgs>? BeforeTunnelConnectRequest; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    /// <summary>
    ///     Intercept tunnel connect response.
    ///     Valid only for explicit endpoints.
    /// </summary>
    public event AsyncEventHandler<TunnelConnectSessionEventArgs>? BeforeTunnelConnectResponse; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    /// <summary>
    ///     Fired when <see cref="TunnelConnectSessionEventArgs.EstablishServerConnectionBeforeResponse" />
    ///     is enabled and upstream connectivity verification fails (DNS, TCP refusal, or upstream-proxy
    ///     CONNECT rejection). Replace <see cref="TunnelConnectFailureEventArgs.Response" /> to customize
    ///     the HTTP error sent to the client before any TLS.
    /// </summary>
    public event AsyncEventHandler<TunnelConnectFailureEventArgs>? BeforeTunnelConnectFailure; // NOSONAR S3264 -- Public extension event invoked by the proxy pipeline.

    internal Task InvokeBeforeTunnelConnectRequest(ProxyServer proxyServer,
        TunnelConnectSessionEventArgs connectArgs, ILogger logger)
    {
        return BeforeTunnelConnectRequest != null
            ? BeforeTunnelConnectRequest.InvokeAsync(proxyServer, connectArgs, logger)
            : Task.CompletedTask;
    }

    internal Task InvokeBeforeTunnelConnectResponse(ProxyServer proxyServer,
        TunnelConnectSessionEventArgs connectArgs, ILogger logger, bool isClientHello = false)
    {
        if (BeforeTunnelConnectResponse == null)
            return Task.CompletedTask;

        connectArgs.IsHttpsConnect = isClientHello;
        return BeforeTunnelConnectResponse.InvokeAsync(proxyServer, connectArgs, logger);
    }

    internal Task InvokeBeforeTunnelConnectFailure(ProxyServer proxyServer,
        TunnelConnectFailureEventArgs failureArgs, ILogger logger)
    {
        return BeforeTunnelConnectFailure != null
            ? BeforeTunnelConnectFailure.InvokeAsync(proxyServer, failureArgs, logger)
            : Task.CompletedTask;
    }
}