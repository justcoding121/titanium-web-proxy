using System;
using System.Collections.Generic;
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
    ///     TCP connects to this host. For re-encrypt (<see cref="ForwardCleartext"/> false),
    ///     TLS SNI and HTTP Host stay on the client authority. For TLS terminate
    ///     (<see cref="ForwardCleartext"/> true), Host is rewritten to this host and
    ///     <see cref="ForwardPort"/> so HTTP origins see their own bind identity.
    /// </summary>
    public string? ForwardHost { get; set; }

    /// <summary>
    ///     Optional fixed upstream port. When null the original request port is used.
    /// </summary>
    public int? ForwardPort { get; set; }

    /// <summary>When true, accepted sockets are copied to <see cref="ForwardHost"/> without HTTP parsing.</summary>
    public bool Passthrough { get; set; }

    private IReadOnlyList<PassthroughSniRoute>? passthroughRoutes;

    /// <summary>
    ///     TCP passthrough only. Routes chosen by the TLS server name in the client's first record.
    ///     Each names a cluster whose destinations are balanced. With no match the connection goes to
    ///     <see cref="PassthroughDefaultClusterId" />, then <see cref="ForwardHost" />.
    /// </summary>
    public IReadOnlyList<PassthroughSniRoute>? PassthroughRoutes
    {
        get => passthroughRoutes;
        set
        {
            passthroughRoutes = value;
            PassthroughTable = value is { Count: > 0 } ? new Network.PassthroughRouteTable(value) : null;
        }
    }

    /// <summary>TCP passthrough only. Cluster used when no SNI route matches, or the client sends no server name.</summary>
    public string? PassthroughDefaultClusterId { get; set; }

    /// <summary>
    ///     TCP passthrough only. How long to wait for the first TLS record when <see cref="PassthroughRoutes" />
    ///     is set. A client that stays silent (a server-speaks-first protocol) is then sent to the default route.
    ///     Zero or negative uses 5 seconds.
    /// </summary>
    public TimeSpan PassthroughSniReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>TCP passthrough only. Closes a connection with no bytes in either direction for this long. Zero disables.</summary>
    public TimeSpan PassthroughIdleTimeout { get; set; }

    /// <summary>TCP passthrough only. Header written to the upstream before the client's bytes.</summary>
    public PassthroughProxyProtocolMode PassthroughProxyProtocol { get; set; }

    private IReadOnlyList<Abstractions.Scripting.IStreamScriptFilter>? streamFilters;

    /// <summary>
    ///     TCP passthrough only. L4 script filters, run in order. Null (the default) or empty keeps the plain relay:
    ///     the accept loop picks the filtered relay only when this holds at least one filter.
    /// </summary>
    public IReadOnlyList<Abstractions.Scripting.IStreamScriptFilter>? StreamFilters
    {
        get => streamFilters;
        set => streamFilters = value is { Count: > 0 } ? value : null;
    }

    internal Network.PassthroughRouteTable? PassthroughTable { get; private set; }

    /// <summary>True when the accept loop should hand sockets to the passthrough relay.</summary>
    internal bool HasPassthroughTarget =>
        Passthrough &&
        (!string.IsNullOrEmpty(ForwardHost) ||
         PassthroughTable is not null ||
         !string.IsNullOrEmpty(PassthroughDefaultClusterId));

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