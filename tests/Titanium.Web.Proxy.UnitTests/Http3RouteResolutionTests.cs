#pragma warning disable TWP001 // Experimental H3 API — intentional in tests
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Dns;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="ProxyServer.ResolveHttp3OriginAsync" /> and
///     <see cref="ProxyServer.ShouldUseHttp3OriginCached" /> covering the complete route-selection
///     precedence described in the plan (section 1 and section 4).
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
    public async Task ResolveH3_H3Disabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: false);
        var route = await server.ResolveHttp3OriginAsync("example.com", 443, null, true, CancellationToken.None);
        Assert.IsFalse(route.UseH3, "H3 disabled → no H3 route.");
    }

    [TestMethod]
    public void CachedRoute_H3Disabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: false);
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ShouldUseHttp3OriginCached("example.com", 443, null);
        Assert.IsFalse(route.UseH3, "H3 disabled → cached entry must be ignored.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Forced Http3 policy
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveH3_ForcedHttp3_ReturnsForced()
    {
        using var server = MakeServer();
        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Http3, false, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.IsTrue(route.ForcedH3, "Forced Http3 policy must set ForcedH3.");
        Assert.AreEqual(Http3RouteSource.Forced, route.Source);
        Assert.AreEqual(443, route.QuicPort);
    }

    [TestMethod]
    public void CachedRoute_ForcedHttp3_ReturnsForced()
    {
        using var server = MakeServer();
        var route = server.ShouldUseHttp3OriginCached("example.com", 443, UpstreamHttpProtocol.Http3);

        Assert.IsTrue(route.UseH3);
        Assert.IsTrue(route.ForcedH3);
        Assert.AreEqual(443, route.QuicPort);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Forced non-H3 policy
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveH3_ForcedHttp11_ReturnsNone()
    {
        using var server = MakeServer();
        // Even with a cache hit, Http11 forces no H3.
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Http11, true, CancellationToken.None);
        Assert.IsFalse(route.UseH3, "Forced Http11 must never select H3.");
    }

    [TestMethod]
    public async Task ResolveH3_ForcedHttp2_ReturnsNone()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Http2, true, CancellationToken.None);
        Assert.IsFalse(route.UseH3, "Forced Http2 must never select H3.");
    }

    [TestMethod]
    public void CachedRoute_ForcedHttp11_ReturnsNone()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");
        var route = server.ShouldUseHttp3OriginCached("example.com", 443, UpstreamHttpProtocol.Http11);
        Assert.IsFalse(route.UseH3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto + Alt-Svc capability cache
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveH3_Auto_CacheHit_ReturnsCachedRoute()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443"); // same-port

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, false, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual(Http3RouteSource.AltSvcCache, route.Source);
        Assert.AreEqual(443, route.QuicPort);
        Assert.IsNull(route.QuicHost, "Alt-Svc entries have no TargetName → QuicHost must be null.");
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheHit_AltPort_ReturnsCachedAltPort()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443", altPort: 8443);

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, false, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual(8443, route.QuicPort);
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheHit_WithTargetName_ReturnsQuicHost()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443",
            altPort: int.MinValue, targetName: "quic-target.cdn.example.com");

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, false, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual("quic-target.cdn.example.com", route.QuicHost);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Auto + SVCB DNS discovery
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbDisabled_ReturnsNone()
    {
        using var server = MakeServer(enableH3: true, enableSvcb: false);
        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, true, CancellationToken.None);
        Assert.IsFalse(route.UseH3, "SVCB disabled + no cache hit → no H3.");
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbHit_ReturnsSvcbRoute()
    {
        var svcb = new SvcbResult(AltPort: 443, Ttl: TimeSpan.FromMinutes(5), TargetName: null);
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: svcb);

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, true, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual(Http3RouteSource.HttpsSvcb, route.Source);
        Assert.AreEqual(443, route.QuicPort);
        Assert.IsNull(route.QuicHost);
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbHit_WithTargetName_SetsQuicHost()
    {
        var svcb = new SvcbResult(AltPort: 443, Ttl: TimeSpan.FromMinutes(5), TargetName: "target.example.com");
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: svcb);

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, true, CancellationToken.None);

        Assert.IsTrue(route.UseH3);
        Assert.AreEqual("target.example.com", route.QuicHost);
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbHit_PopulatesCache()
    {
        var svcb = new SvcbResult(AltPort: 8443, Ttl: TimeSpan.FromMinutes(10), TargetName: null);
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: svcb);

        await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, true, CancellationToken.None);

        // Subsequent cache-only check must succeed without DNS.
        var cachedRoute = server.ShouldUseHttp3OriginCached("example.com", 443, UpstreamHttpProtocol.Auto);
        Assert.IsTrue(cachedRoute.UseH3, "SVCB result must be stored in capability cache.");
        Assert.AreEqual(8443, cachedRoute.QuicPort);
    }

    [TestMethod]
    public async Task ResolveH3_Auto_CacheMiss_SvcbMiss_ReturnsNone()
    {
        using var server = MakeServer(enableH3: true, enableSvcb: true, svcbResult: null);
        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, UpstreamHttpProtocol.Auto, true, CancellationToken.None);
        Assert.IsFalse(route.UseH3, "SVCB returns null → no H3.");
    }

    [TestMethod]
    public async Task ResolveH3_AllowDnsProbe_False_SkipsDnsEvenIfEnabled()
    {
        // DNS probe is blocked on the per-stream hot path even when SVCB discovery is enabled.
        var resolver = new CountingResolver(new SvcbResult(443, TimeSpan.FromMinutes(1)));
        // EnableHttp3=true → EnableHttpsSvcbDnsDiscovery defaults to true; install counting resolver.
        var server = new ProxyServer(false, false, false) { EnableHttp3 = true };
        server.HttpsSvcbResolver = resolver;

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, null, allowDnsProbe: false, CancellationToken.None);

        Assert.IsFalse(route.UseH3, "allowDnsProbe=false must skip DNS.");
        Assert.AreEqual(0, resolver.ProbeCount, "DNS probe must not be invoked.");
        server.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Null protocol treated as Auto
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ResolveH3_NullProtocol_TreatedAsAuto()
    {
        using var server = MakeServer();
        server.Http3OriginCapabilityCache.Set("example.com:443");

        var route = await server.ResolveHttp3OriginAsync(
            "example.com", 443, effectiveProtocol: null, false, CancellationToken.None);

        Assert.IsTrue(route.UseH3, "null protocol → Auto → cache hit → H3.");
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
    }
}
