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
                Http3OriginCapabilityCache.Evict($"{args.HttpClient.Request.RequestUri?.Host}:{args.HttpClient.Request.RequestUri?.Port}");
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
    ///     This is the single authority for H3 route selection; use it from every protocol path
    ///     rather than calling <see cref="ShouldUseHttp3OriginCached"/> directly or duplicating
    ///     the cache/DNS logic.
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
    ///     When <see langword="true"/>, a capability-cache miss may trigger an HTTPS/SVCB DNS
    ///     lookup (only when <see cref="EnableHttpsSvcbDnsDiscovery"/> is also
    ///     <see langword="true"/>). Set to <see langword="false"/> on the hot per-stream path
    ///     inside a running H2 relay to avoid blocking the frame reader on DNS I/O.
    /// </param>
    /// <param name="cancellationToken">Propagates request/connection cancellation.</param>
    internal async Task<Http3OriginRoute> ResolveHttp3OriginAsync(
        string host, int port,
        UpstreamHttpProtocol? effectiveProtocol,
        bool allowDnsProbe,
        CancellationToken cancellationToken)
    {
        if (!EnableHttp3) return Http3OriginRoute.None;

        var protocol = effectiveProtocol ?? UpstreamHttpProtocol.Auto;

        // Explicit forced H3 — callers must handle QUIC failure; no TCP fallback.
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

        // Auto + DNS probe (connection-time path only; never inside H2 frame-reading loop).
        if (allowDnsProbe && EnableHttpsSvcbDnsDiscovery)
        {
            var svcb = await HttpsSvcbResolver.TryGetH3CapabilityAsync(host, port, cancellationToken);
            if (svcb != null)
            {
                // Normalize same-port vs alternative-port for the capability cache.
                var altPort = svcb.AltPort == port ? int.MinValue : svcb.AltPort;
                Http3OriginCapabilityCache.Set(hostAndPort, altPort, svcb.Ttl, svcb.TargetName);
                return new Http3OriginRoute
                {
                    UseH3 = true,
                    QuicPort = svcb.AltPort,
                    QuicHost = svcb.TargetName, // null when SVCB uses "." (owner name)
                    Source = Http3RouteSource.HttpsSvcb
                };
            }
            // Negative result already cached internally by UdpSvcbDnsResolver.
        }

        return Http3OriginRoute.None;
    }

    /// <summary>
    ///     Synchronous, cache-only variant of <see cref="ResolveHttp3OriginAsync" /> for use on
    ///     the hot per-stream path inside a running H2 relay where DNS I/O must not block the
    ///     frame reader. Returns <see cref="Http3OriginRoute.None"/> on any cache miss.
    /// </summary>
    internal Http3OriginRoute ShouldUseHttp3OriginCached(
        string host, int port, UpstreamHttpProtocol? effectiveProtocol)
    {
        if (!EnableHttp3) return Http3OriginRoute.None;

        var protocol = effectiveProtocol ?? UpstreamHttpProtocol.Auto;

        if (protocol == UpstreamHttpProtocol.Http3)
            return new Http3OriginRoute
            {
                UseH3 = true,
                QuicPort = port,
                ForcedH3 = true,
                Source = Http3RouteSource.Forced
            };

        if (protocol == UpstreamHttpProtocol.Http11 || protocol == UpstreamHttpProtocol.Http2)
            return Http3OriginRoute.None;

        var hostAndPort = $"{host}:{port}";
        if (Http3OriginCapabilityCache.TryGet(hostAndPort, out var cachedAltPort, out var cachedTarget))
        {
            var quicPort = cachedAltPort == int.MinValue ? port : cachedAltPort;
            return new Http3OriginRoute
            {
                UseH3 = true,
                QuicPort = quicPort,
                QuicHost = cachedTarget,
                Source = Http3RouteSource.AltSvcCache
            };
        }

        return Http3OriginRoute.None;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when this request should be forwarded to the origin over
    ///     HTTP/3 rather than the normal TCP pipeline. Evaluated in <c>HandleHttpSessionRequest</c>
    ///     before the TCP connection generator runs.
    ///     <para>
    ///         This is a thin synchronous wrapper used only on the H1.1 hot path; prefer
    ///         <see cref="ResolveHttp3OriginAsync" /> on paths where async DNS probing is safe.
    ///     </para>
    /// </summary>
    internal bool ShouldUseHttp3Origin(SessionEventArgs args)
    {
        if (!EnableHttp3) return false;

        var effectiveProtocol = args.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;

        if (effectiveProtocol == UpstreamHttpProtocol.Http3) return true;

        if (effectiveProtocol == UpstreamHttpProtocol.Auto)
        {
            var host = args.HttpClient.Request.RequestUri?.Host;
            var port = args.HttpClient.Request.RequestUri?.Port ?? 443;
            if (host != null && Http3OriginCapabilityCache.TryGet($"{host}:{port}", out _))
                return true;
        }

        return false;
    }
}
