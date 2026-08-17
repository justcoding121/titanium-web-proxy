#pragma warning disable TWP001 // Experimental H3 API — intentional in tests
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Dns;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="ProxyServer.ResolveHttp3Origin" /> covering the complete
///     route-selection precedence described in the plan (section 1 and section 4), in both the
///     warming (<c>allowDnsProbe: true</c>) and cache-only (<c>allowDnsProbe: false</c>) modes.
/// </summary>
[TestClass]
public class Http3RouteResolutionTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Stub resolver that always returns a fixed <see cref="SvcbResult" />.
    /// </summary>
    private sealed class StubSvcbResolver : IHttpsSvcbResolver
    {
        private readonly SvcbResult? _result;
        internal StubSvcbResolver(SvcbResult? result) => _result = result;
        public Task<SvcbResult?> TryGetH3CapabilityAsync(string host, int port, CancellationToken ct)
            => Task.FromResult(_result);
        public void TrimExpired() { }
    }

    private static ProxyServer MakeServer(bool enableH3 = true, bool enableSvcb = false,
        SvcbResult? svcbResult = null)
    {
        var s = new ProxyServer(false, false, false) { EnableHttp3 = enableH3 };

        if (enableSvcb)
        {
            // Explicit opt-in: use the stub resolver.
            s.EnableHttpsSvcbDnsDiscovery = true;
            s.HttpsSvcbResolver = new StubSvcbResolver(svcbResult);
        }
        else
        {
            // Explicit opt-out: override the new default (EnableH3 => EnableSvcb) so tests that
            // want "H3 on, no SVCB probe" remain deterministic without real DNS calls.
            s.EnableHttpsSvcbDnsDiscovery = false;
        }

        return s;
    }

    /// <summary>
    ///     Pretends a QUIC connection to the origin is already established. Route resolution requires
    ///     both a capability-cache entry and a live connection before it will send a request over
    ///     HTTP/3, so tests that care about the resulting route must set this up; tests that omit it
    ///     are exercising the cold path, which defers to TCP.
    /// </summary>
    private static void MarkOriginWarm(ProxyServer server, string host, int port)
        => server.Http3WarmOrigins.Mark(host, port);

    // ─────────────────────────────────────────────────────────────────────────
    // EnableHttpsSvcbDnsDiscovery defaults to EnableHttp3
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void EnableHttpsSvcbDnsDiscovery_DefaultsToFalse_WhenH3IsOff()
    {
        using var server = new ProxyServer(false, false, false);
        Assert.IsFalse(server.EnableHttpsSvcbDnsDiscovery,
            "SVCB discovery must be off when H3 is disabled.");
    }

    [TestMethod]
    public void EnableHttpsSvcbDnsDiscovery_DefaultsToTrue_WhenH3IsOn()
    {
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        Assert.IsTrue(server.EnableHttpsSvcbDnsDiscovery,
            "SVCB discovery must default to true when H3 is enabled, so the first connection can use H3.");
    }

    [TestMethod]
    public void EnableHttpsSvcbDnsDiscovery_ExplicitFalse_OverridesDefault()
    {
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.EnableHttpsSvcbDnsDiscovery = false;
        Assert.IsFalse(server.EnableHttpsSvcbDnsDiscovery,
            "Explicit false must override the H3-inherited default (escape hatch for untrusted DNS).");
    }

    [TestMethod]
    public void EnableHttpsSvcbDnsDiscovery_ExplicitTrue_WithH3Off_IsRespected()
    {
        // Unusual but legal: manually enable SVCB even if H3 is off.
        using var server = new ProxyServer(false, false, false);
        server.EnableHttpsSvcbDnsDiscovery = true;
        Assert.IsTrue(server.EnableHttpsSvcbDnsDiscovery);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EnableHttp3 = false
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_H3Disabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: false);
        var route = server.ResolveHttp3Origin("example.com", 443, null, true);
        Assert.IsFalse(route.UseH3, "H3 disabled → no H3 route.");
    }

    [TestMethod]
    public void CachedRoute_H3Disabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: false);
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ResolveHttp3Origin("example.com", 443, null, allowDnsProbe: false);
        Assert.IsFalse(route.UseH3, "H3 disabled → cached entry must be ignored.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Forced Http3 policy
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_ForcedHttp3_ReturnsForced()
    {
        using var server = MakeServer();
        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Http3, false);

        Assert.IsTrue(route.UseH3);
        Assert.IsTrue(route.ForcedH3, "Forced Http3 policy must set ForcedH3.");
        Assert.AreEqual(Http3RouteSource.Forced, route.Source);
        Assert.AreEqual(443, route.QuicPort);
    }

    [TestMethod]
    public void CachedRoute_ForcedHttp3_ReturnsForced()
    {
        using var server = MakeServer();
        var route = server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Http3,
            allowDnsProbe: false);

        Assert.IsTrue(route.UseH3);
        Assert.IsTrue(route.ForcedH3);
        Assert.AreEqual(443, route.QuicPort);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Forced non-H3 policy
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_ForcedHttp11_ReturnsNone()
    {
        using var server = MakeServer();
        // Even with a cache hit, Http11 forces no H3.
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Http11, true);
        Assert.IsFalse(route.UseH3, "Forced Http11 must never select H3.");
    }

    [TestMethod]
    public void ResolveH3_ForcedHttp2_ReturnsNone()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Http2, true);
        Assert.IsFalse(route.UseH3, "Forced Http2 must never select H3.");
    }

    [TestMethod]
    public void CachedRoute_ForcedHttp11_ReturnsNone()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Http11,
            allowDnsProbe: false);
        Assert.IsFalse(route.UseH3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto + Alt-Svc capability cache
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_Auto_CacheHit_ReturnsCachedRoute()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443"); // same-port
        MarkOriginWarm(server, "example.com", 443);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, false);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual(Http3RouteSource.AltSvcCache, route.Source);
        Assert.AreEqual(443, route.QuicPort);
        Assert.IsNull(route.QuicHost, "Alt-Svc entries have no TargetName → QuicHost must be null.");
    }

    [TestMethod]
    public void ResolveH3_Auto_CacheHit_AltPort_ReturnsCachedAltPort()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443", altPort: 8443);
        // Warm-tracking is keyed by the port QUIC actually connects on, not the origin port.
        MarkOriginWarm(server, "example.com", 8443);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, false);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual(8443, route.QuicPort);
    }

    [TestMethod]
    public void ResolveH3_Auto_CacheHit_WithTargetName_ReturnsQuicHost()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443",
            altPort: int.MinValue, targetName: "quic-target.cdn.example.com");
        MarkOriginWarm(server, "example.com", 443);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, false);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual("quic-target.cdn.example.com", route.QuicHost);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto + capability cache, but no established QUIC connection yet
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_Auto_CacheHit_ColdOrigin_StaysOnTcp()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, false);

        Assert.IsFalse(route.UseH3,
            "Knowing the origin speaks H3 is not enough: routing there before a QUIC connection " +
            "exists would charge this request for the handshake.");
        Assert.AreEqual(Http3RouteSource.None, route.Source);
    }

    [TestMethod]
    public void ResolveH3_Auto_CacheHit_BecomesH3OnceOriginIsWarm()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");

        Assert.IsFalse(
            server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, false).UseH3);

        MarkOriginWarm(server, "example.com", 443);

        Assert.IsTrue(
            server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, false).UseH3,
            "Once a connection is established the handshake is already paid for.");
    }

    [TestMethod]
    public void ResolveH3_Forced_ColdOrigin_StillUsesH3()
    {
        using var server = MakeServer();

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Http3, false);

        Assert.IsTrue(route.UseH3, "Forced H3 is an explicit instruction; it does not wait to warm.");
        Assert.IsTrue(route.ForcedH3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto + SVCB DNS discovery
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_Auto_CacheMiss_SvcbDisabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: true, enableSvcb: false);
        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, true);
        Assert.IsFalse(route.UseH3, "SVCB disabled + no cache hit → no H3.");
    }

    [TestMethod]
    public void ResolveH3_Auto_CacheMiss_SvcbEnabled_ReturnsNoneImmediately()
    {
        // Auto-mode never awaits DNS on the critical path: first connection falls through to H2/H1.
        var svcb = new SvcbResult(AltPort: 443, Ttl: TimeSpan.FromMinutes(5), TargetName: null);
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: svcb);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, true);

        Assert.IsFalse(route.UseH3, "Cache miss must not block CONNECT waiting for SVCB.");
        Assert.AreEqual(Http3RouteSource.None, route.Source);
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbHit_WarmsCacheInBackground()
    {
        var svcb = new SvcbResult(AltPort: 8443, Ttl: TimeSpan.FromMinutes(10), TargetName: "target.example.com");
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: svcb);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, true);
        Assert.IsFalse(route.UseH3);

        // Background discovery should populate the capability cache for subsequent connections.
        // Marking the origin warm isolates this test to the SVCB result: without it, resolution
        // would keep deferring to TCP no matter how good the cache entry is.
        MarkOriginWarm(server, "example.com", 8443);

        Http3OriginRoute cachedRoute = Http3OriginRoute.None;
        for (var i = 0; i < 50; i++)
        {
            cachedRoute = server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto,
                allowDnsProbe: false);
            if (cachedRoute.UseH3) break;
            await Task.Delay(20);
        }

        Assert.IsTrue(cachedRoute.UseH3, "Background SVCB result must warm the capability cache.");
        Assert.AreEqual(8443, cachedRoute.QuicPort);
        Assert.AreEqual("target.example.com", cachedRoute.QuicHost);
    }

    [TestMethod]
    public void ResolveH3_Auto_CacheMiss_SvcbMiss_ReturnsNone()
    {
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: null);
        var route = server.ResolveHttp3Origin(
            "example.com", 443, UpstreamHttpProtocol.Auto, true);
        Assert.IsFalse(route.UseH3, "SVCB miss → no H3 on the current connection.");
    }

    [TestMethod]
    public async Task ResolveH3_Auto_ConcurrentMisses_CoalesceToOneDiscovery()
    {
        var resolver = new CountingResolver(new SvcbResult(443, TimeSpan.FromMinutes(1)));
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.EnableHttpsSvcbDnsDiscovery = true;
        server.HttpsSvcbResolver = resolver;

        server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);
        server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);
        server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);

        for (var i = 0; i < 50 && resolver.ProbeCount == 0; i++)
            await Task.Delay(20);

        Assert.AreEqual(1, resolver.ProbeCount, "Concurrent misses must share one background discovery.");
    }

    [TestMethod]
    public async Task ResolveH3_Auto_RepeatedMissesAfterCompletion_SuppressFurtherProbes()
    {
        // Sequential (non-concurrent) misses for the same host must not each spawn a fresh
        // background discovery once one has just completed negatively.
        var resolver = new CountingResolver(null);
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.EnableHttpsSvcbDnsDiscovery = true;
        server.HttpsSvcbResolver = resolver;

        server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);
        for (var i = 0; i < 50 && resolver.ProbeCount == 0; i++)
            await Task.Delay(20);
        Assert.AreEqual(1, resolver.ProbeCount, "First miss must probe once.");

        // Wait until miss suppression is recorded (ProbeCount stays at 1 across further resolves).
        var suppressed = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);
            if (resolver.ProbeCount == 1)
            {
                // Confirm it stays suppressed across another resolve + short settle.
                await Task.Delay(20);
                server.ResolveHttp3Origin("example.com", 443, UpstreamHttpProtocol.Auto, true);
                if (resolver.ProbeCount == 1)
                {
                    suppressed = true;
                    break;
                }
            }

            await Task.Delay(20);
        }

        Assert.IsTrue(suppressed,
            "A recent miss must suppress further background discovery for the same host:port.");
        Assert.AreEqual(1, resolver.ProbeCount);
    }

    [TestMethod]
    public void HttpsSvcbResolver_NoUsableDnsServer_DoesNotThrow()
    {
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.DnsServerEndPoint = new IPEndPoint(IPAddress.None, 0);

        var resolver = server.HttpsSvcbResolver;

        Assert.IsInstanceOfType<NoOpHttpsSvcbResolver>(resolver,
            "No usable DNS server → resolver must be the safe no-op, not an exception.");
    }

    [TestMethod]
    public async Task HttpsSvcbResolver_NoUsableDnsServer_AlwaysReportsMiss()
    {
        using var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.DnsServerEndPoint = new IPEndPoint(IPAddress.None, 0);

        var result = await server.HttpsSvcbResolver.TryGetH3CapabilityAsync("example.com", 443, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveH3_AllowDnsProbe_False_SkipsDnsEvenIfEnabled()
    {
        // DNS probe is blocked on the per-stream hot path even when SVCB discovery is enabled.
        var resolver = new CountingResolver(new SvcbResult(443, TimeSpan.FromMinutes(1)));
        // EnableHttp3=true → EnableHttpsSvcbDnsDiscovery defaults to true; install counting resolver.
        var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.HttpsSvcbResolver = resolver;

        var route = server.ResolveHttp3Origin(
            "example.com", 443, null, allowDnsProbe: false);

        Assert.IsFalse(route.UseH3, "allowDnsProbe=false must skip DNS.");
        Assert.AreEqual(0, resolver.ProbeCount, "DNS probe must not be invoked.");
        server.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Null protocol treated as Auto
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveH3_NullProtocol_TreatedAsAuto()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");
        MarkOriginWarm(server, "example.com", 443);

        var route = server.ResolveHttp3Origin(
            "example.com", 443, effectiveProtocol: null, false);

        Assert.IsTrue(route.UseH3, "null protocol → Auto → cache hit → warm origin → H3.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3OriginRoute.None sentinel
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Http3OriginRoute_None_HasUseH3False()
    {
        var none = Http3OriginRoute.None;
        Assert.IsFalse(none.UseH3);
        Assert.IsFalse(none.ForcedH3);
        Assert.AreEqual(Http3RouteSource.None, none.Source);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class CountingResolver : IHttpsSvcbResolver
    {
        private readonly SvcbResult? _result;
        internal int ProbeCount;

        internal CountingResolver(SvcbResult? result) => _result = result;

        public Task<SvcbResult?> TryGetH3CapabilityAsync(string host, int port, CancellationToken ct)
        {
            ProbeCount++;
            return Task.FromResult(_result);
        }

        public void TrimExpired() { }
    }
}
