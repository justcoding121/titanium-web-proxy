using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     After every response, inspect the <c>Alt-Svc</c> header and cache any HTTP/3 capability
    ///     advertised by the origin. This allows subsequent requests to the same host to use HTTP/3
    ///     proactively (when <see cref="EnableHttp3" /> is <see langword="true" /> and
    ///     <see cref="Models.UpstreamHttpProtocol.Auto" /> is in effect).
    /// </summary>
    private void TryUpdateHttp3CapabilityFromResponse(SessionEventArgs args)
    {
        if (!EnableHttp3) return;

        var response = args.HttpClient.Response;
        if (response == null) return;

        var altSvc = response.Headers.GetHeaderValueOrNull("Alt-Svc");
        if (string.IsNullOrEmpty(altSvc) || altSvc == "clear")
        {
            if (altSvc == "clear")
            {
                var clearKey =
                    $"{args.HttpClient.Request.RequestUri?.Host}:{args.HttpClient.Request.RequestUri?.Port}";
                Http3OriginCapabilityCache.Evict(clearKey);
                // Prevent a late background SVCB completion from undoing the clear.
                _svcbDiscoveryCoordinator?.Invalidate(clearKey);
            }

            return;
        }

        var entries = AltSvcParser.Parse(altSvc);
        if (entries.Count == 0) return;

        var host = args.HttpClient.Request.RequestUri?.Host;
        var port = args.HttpClient.Request.RequestUri?.Port ?? 443;
        if (string.IsNullOrEmpty(host)) return;

        var hostAndPort = $"{host}:{port}";

        foreach (var entry in entries)
        {
            if (entry.MaxAgeSeconds <= 0) continue;

            var altPort = entry.Port == port ? int.MinValue : entry.Port;
            var ttl = TimeSpan.FromSeconds(Math.Min(entry.MaxAgeSeconds, Http3OriginCapabilityCache.DefaultTtl.TotalSeconds * 2));
            // Alt-Svc does not carry a TargetName — always null here.
            Http3OriginCapabilityCache.Set(hostAndPort, altPort, ttl, targetName: null);
            break; // Take the first valid h3 entry.
        }
    }

    /// <summary>
    ///     Resolves whether the proxy should connect to the origin over HTTP/3 for the given
    ///     <paramref name="host"/>:<paramref name="port"/> and effective upstream protocol policy.
    ///     This is the single authority for H3 route selection; every protocol path goes through it
    ///     rather than duplicating the cache lookup.
    ///     <para>
    ///         Resolution is always synchronous and allocation-free on the cache path: no caller ever
    ///         awaits DNS. Auto-mode SVCB discovery is fire-and-forget through
    ///         <see cref="SvcbDiscoveryCoordinator" />; forced HTTP/3 never queries DNS at all.
    ///     </para>
    /// </summary>
    /// <param name="host">Origin host name.</param>
    /// <param name="port">Origin port.</param>
    /// <param name="effectiveProtocol">
    ///     The upstream protocol policy in effect (connection-level override from
    ///     <c>BeforeTunnelConnectRequest</c> / <c>BeforeSslAuthenticate</c>, or the per-stream
    ///     override from <c>BeforeRequest</c>). <see langword="null"/> is treated as
    ///     <see cref="UpstreamHttpProtocol.Auto"/>.
    /// </param>
    /// <param name="allowDnsProbe">
    ///     When <see langword="true"/>, a capability-cache miss queues a background HTTPS/SVCB DNS
    ///     lookup (only when <see cref="EnableHttpsSvcbDnsDiscovery"/> is also
    ///     <see langword="true"/>) that warms the cache for later connections. The current call still
    ///     returns <see cref="Http3OriginRoute.None"/> on a miss, so CONNECT/request latency is
    ///     unaffected either way. Pass <see langword="false"/> on the hot per-stream path inside a
    ///     running H2 relay, where even queueing work per stream is unwanted.
    /// </param>
    internal Http3OriginRoute ResolveHttp3Origin(
        string host, int port,
        UpstreamHttpProtocol? effectiveProtocol,
        bool allowDnsProbe)
    {
        if (!EnableHttp3) return Http3OriginRoute.None;

        var protocol = effectiveProtocol ?? UpstreamHttpProtocol.Auto;

        // Explicit forced H3 — callers must handle QUIC failure; no TCP fallback. No DNS.
        if (protocol == UpstreamHttpProtocol.Http3)
            return new Http3OriginRoute
            {
                UseH3 = true,
                QuicPort = port,
                ForcedH3 = true,
                Source = Http3RouteSource.Forced
            };

        // Explicit non-H3 policy — honour without any DNS query.
        if (protocol == UpstreamHttpProtocol.Http11 || protocol == UpstreamHttpProtocol.Http2)
            return Http3OriginRoute.None;

        // Auto: check the in-memory Alt-Svc / SVCB capability cache (synchronous, no I/O).
        var hostAndPort = $"{host}:{port}";
        if (Http3OriginCapabilityCache.TryGet(hostAndPort, out var cachedAltPort, out var cachedTarget))
        {
            var quicPort = cachedAltPort == int.MinValue ? port : cachedAltPort;
            return new Http3OriginRoute
            {
                UseH3 = true,
                QuicPort = quicPort,
                QuicHost = cachedTarget, // null when cache entry came from Alt-Svc
                Source = Http3RouteSource.AltSvcCache
            };
        }

        // Auto + discovery: queue background SVCB and return immediately. Never await DNS on the
        // CONNECT / request critical path — the first connection uses H2/H1; subsequent ones may
        // upgrade once the capability cache is warm (or Alt-Svc arrives on the first response).
        if (allowDnsProbe && EnableHttpsSvcbDnsDiscovery)
            SvcbDiscoveryCoordinator.QueueDiscovery(host, port);

        return Http3OriginRoute.None;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when this request should be forwarded to the origin over
    ///     HTTP/3 rather than the normal TCP pipeline. Evaluated in <c>HandleHttpSessionRequest</c>
    ///     before the TCP connection generator runs. Cache-only: this path does not warm SVCB.
    /// </summary>
    internal bool ShouldUseHttp3Origin(SessionEventArgs args)
    {
        var host = args.HttpClient.Request.RequestUri?.Host ?? string.Empty;
        var port = args.HttpClient.Request.RequestUri?.Port ?? 443;

        return ResolveHttp3Origin(host, port, args.UpstreamHttpProtocol, allowDnsProbe: false).UseH3;
    }
}
