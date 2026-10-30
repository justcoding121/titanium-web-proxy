using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Rate limit v2: key types, token bucket, Retry-After, RateLimit-* headers, key cap.</summary>
[TestClass]
[DoNotParallelize]
public class RateLimitV2Tests
{
    private static readonly TransparentProxyEndPoint EndPoint = new(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 };
    private static readonly string[] Trusted = ["10.0.0.0/8"];

    private ManualTimeProvider clock = null!;

    [TestInitialize]
    public void Reset()
    {
        RoutePolicies.ResetForTests();
        clock = new ManualTimeProvider();
        RoutePolicies.Clock = clock;
    }

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    private static ReverseProxyOptions Options(RoutePolicyConfig policy, string routeId = "r") => new()
    {
        Policies = [policy],
        Routes =
        [
            new RouteConfig
            {
                Id = routeId,
                ClusterId = "c",
                Policies = [policy.Name],
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
    };

    private static RoutePolicyConfig Rate(Action<Builder>? configure = null)
    {
        var builder = new Builder();
        configure?.Invoke(builder);
        return new RoutePolicyConfig
        {
            Name = "rate",
            Kind = "rateLimit",
            Limit = builder.Limit,
            WindowSeconds = builder.WindowSeconds,
            Key = builder.Key,
            Algorithm = builder.Algorithm,
            Burst = builder.Burst,
            MaxKeys = builder.MaxKeys,
            EmitHeaders = builder.EmitHeaders,
        };
    }

    private sealed class Builder
    {
        public int Limit { get; set; } = 2;
        public int WindowSeconds { get; set; } = 10;
        public string? Key { get; set; }
        public string? Algorithm { get; set; }
        public int? Burst { get; set; }
        public int? MaxKeys { get; set; }
        public bool? EmitHeaders { get; set; }
    }

    private static Request Req(params (string Name, string Value)[] headers)
    {
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/x", Host = "app" };
        foreach (var (name, value) in headers)
        {
            request.Headers.AddHeader(name, value);
        }

        return request;
    }

    private static bool Denied(
        ReverseProxyOptions options,
        Request request,
        string client,
        out List<KeyValuePair<string, string>>? headers,
        IReadOnlyList<string>? trusted = null)
    {
        var denied = RoutePolicies.TryHandleLite(
            options, EndPoint, request, new IPEndPoint(IPAddress.Parse(client), 1000),
            out var status, out _, out headers, trusted);
        if (denied)
        {
            Assert.AreEqual(429, status);
        }

        return denied;
    }

    private static bool Denied(ReverseProxyOptions options, Request request, string client, IReadOnlyList<string>? trusted = null) =>
        Denied(options, request, client, out _, trusted);

    private static string Header(List<KeyValuePair<string, string>>? headers, string name) =>
        headers!.Single(h => h.Key == name).Value;

    [TestMethod]
    public void HeaderKey_IsolatesValues_AndIgnoresTheClientAddress()
    {
        var options = Options(Rate(b => b.Key = "header:X-Api-Key"));
        var a = Req(("X-Api-Key", "alpha"));
        var b = Req(("X-Api-Key", "beta"));

        Assert.IsFalse(Denied(options, a, "10.0.0.1"));
        Assert.IsFalse(Denied(options, a, "10.0.0.2"), "same value from another address shares the counter");
        Assert.IsTrue(Denied(options, a, "10.0.0.3"));
        Assert.IsFalse(Denied(options, b, "10.0.0.1"), "another value has its own counter");
        Assert.IsFalse(Denied(options, b, "10.0.0.1"));
        Assert.IsTrue(Denied(options, b, "10.0.0.1"));
    }

    [TestMethod]
    public void HeaderKey_WithoutTheHeader_FallsBackToTheClientAddress()
    {
        var options = Options(Rate(b => b.Key = "header:X-Api-Key"));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"), "leaving the header out does not escape the limit");
        Assert.IsFalse(Denied(options, Req(), "10.0.0.2"));
    }

    [TestMethod]
    public void CookieKey_SelectsTheNamedCookie()
    {
        var options = Options(Rate(b => { b.Key = "cookie:sid"; b.Limit = 1; }));
        var one = Req(("Cookie", "theme=dark; sid=abc; other=1"));
        var two = Req(("Cookie", "sid=def"));
        var none = Req(("Cookie", "theme=dark"));

        Assert.IsFalse(Denied(options, one, "10.0.0.1"));
        Assert.IsTrue(Denied(options, one, "10.0.0.2"));
        Assert.IsFalse(Denied(options, two, "10.0.0.1"));
        Assert.IsFalse(Denied(options, none, "10.0.0.9"));
        Assert.IsTrue(Denied(options, none, "10.0.0.9"), "no sid cookie counts by address");
    }

    [TestMethod]
    public void RouteKey_SharesOneCounterAcrossClients_PerRoute()
    {
        var policy = Rate(b => { b.Key = "route"; b.Limit = 2; });
        var options = Options(policy);
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.2"));
        Assert.IsTrue(Denied(options, Req(), "10.0.0.3"));

        var otherRoute = Options(policy, "other");
        Assert.IsFalse(Denied(otherRoute, Req(), "10.0.0.3"), "a different route has its own counter");
    }

    [TestMethod]
    public void ForwardedClient_UsesTheRightmostUntrustedEntry_WhenThePeerIsTrusted()
    {
        var options = Options(Rate(b => { b.Key = "forwardedClient"; b.Limit = 1; }));

        // Peer 10.0.0.5 is a trusted proxy. 10.0.0.9 is trusted too, so 203.0.113.7 is the client.
        var first = Req(("X-Forwarded-For", "198.51.100.1, 203.0.113.7, 10.0.0.9"));
        Assert.IsFalse(Denied(options, first, "10.0.0.5", Trusted));
        Assert.IsTrue(Denied(options, first, "10.0.0.5", Trusted));

        // A different real client behind the same proxy is counted on its own.
        var second = Req(("X-Forwarded-For", "203.0.113.8"));
        Assert.IsFalse(Denied(options, second, "10.0.0.5", Trusted));
    }

    [TestMethod]
    public void ForwardedClient_IgnoresASpoofedChain_FromAnUntrustedPeer()
    {
        var options = Options(Rate(b => { b.Key = "forwardedClient"; b.Limit = 1; }));

        // The peer is not a trusted proxy, so rotating X-Forwarded-For does not buy a new counter.
        Assert.IsFalse(Denied(options, Req(("X-Forwarded-For", "1.1.1.1")), "198.51.100.50", Trusted));
        Assert.IsTrue(Denied(options, Req(("X-Forwarded-For", "2.2.2.2")), "198.51.100.50", Trusted));
        Assert.IsTrue(Denied(options, Req(), "198.51.100.50", Trusted));
    }

    [TestMethod]
    public void ForwardedClient_WithoutTrustedProxies_UsesTheSocketAddress()
    {
        var options = Options(Rate(b => { b.Key = "forwardedClient"; b.Limit = 1; }));
        Assert.IsFalse(Denied(options, Req(("X-Forwarded-For", "1.1.1.1")), "10.0.0.5"));
        Assert.IsTrue(Denied(options, Req(("X-Forwarded-For", "2.2.2.2")), "10.0.0.5"));
    }

    [TestMethod]
    public void ForwardedClient_FallsBackToThePeer_ForAnEntirelyTrustedOrUnparseableChain()
    {
        var peer = IPAddress.Parse("10.0.0.5");
        Assert.AreEqual(peer, RateLimiter.ResolveForwardedClient(Req(("X-Forwarded-For", "10.0.0.7, 10.0.0.8")), peer, Trusted));
        Assert.AreEqual(peer, RateLimiter.ResolveForwardedClient(Req(("X-Forwarded-For", "garbage")), peer, Trusted));
        Assert.AreEqual(peer, RateLimiter.ResolveForwardedClient(Req(("X-Forwarded-For", "1.1.1.1, unknown")), peer, Trusted));
        Assert.AreEqual(peer, RateLimiter.ResolveForwardedClient(Req(), peer, Trusted));
        Assert.AreEqual(IPAddress.Parse("203.0.113.9"), RateLimiter.ResolveForwardedClient(Req(("X-Forwarded-For", "203.0.113.9:4711")), peer, Trusted));
        Assert.AreEqual(IPAddress.Parse("2001:db8::1"), RateLimiter.ResolveForwardedClient(Req(("X-Forwarded-For", "[2001:db8::1]:443")), peer, Trusted));
        Assert.AreEqual(IPAddress.Parse("203.0.113.9"), RateLimiter.ResolveForwardedClient(
            Req(("X-Forwarded-For", "9.9.9.9"), ("X-Forwarded-For", "203.0.113.9, 10.0.0.8")), peer, Trusted),
            "several header lines are read as one chain");
    }

    [TestMethod]
    public void TokenBucket_AllowsABurst_ThenRefillsAtTheConfiguredRate()
    {
        // 2 tokens per 10 s refill 1 token every 5 s. The bucket holds 4.
        var options = Options(Rate(b => { b.Algorithm = "tokenBucket"; b.Limit = 2; b.WindowSeconds = 10; b.Burst = 4; }));
        for (var i = 0; i < 4; i++)
        {
            Assert.IsFalse(Denied(options, Req(), "10.0.0.1"), $"burst request {i}");
        }

        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.2"), "another client has a full bucket");

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"), "0.8 of a token is not enough");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"), "one token has refilled");
        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(600));
        for (var i = 0; i < 4; i++)
        {
            Assert.IsFalse(Denied(options, Req(), "10.0.0.1"), $"refill is capped at the burst, request {i}");
        }

        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"));
    }

    [TestMethod]
    public void TokenBucket_BurstDefaultsToTheLimit()
    {
        var options = Options(Rate(b => { b.Algorithm = "tokenBucket"; b.Limit = 3; }));
        for (var i = 0; i < 3; i++)
        {
            Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        }

        Assert.IsTrue(Denied(options, Req(), "10.0.0.1"));
    }

    [TestMethod]
    public void RetryAfter_TellsHowLongUntilTheWindowEnds_OrATokenIsBack()
    {
        var fixedWindow = Options(Rate(b => { b.Limit = 1; b.WindowSeconds = 10; }));
        Assert.IsFalse(Denied(fixedWindow, Req(), "10.0.0.1"));
        clock.Advance(TimeSpan.FromSeconds(3.5));
        Assert.IsTrue(Denied(fixedWindow, Req(), "10.0.0.1", out var headers));
        Assert.AreEqual("7", Header(headers, "Retry-After"), "6.5 s left rounds up");
        Assert.AreEqual(1, headers!.Count, "no RateLimit-* unless asked");

        RoutePolicies.ResetForTests();
        RoutePolicies.Clock = clock;
        var bucket = Options(Rate(b => { b.Algorithm = "tokenBucket"; b.Limit = 1; b.WindowSeconds = 10; }));
        Assert.IsFalse(Denied(bucket, Req(), "10.0.0.1"));
        Assert.IsTrue(Denied(bucket, Req(), "10.0.0.1", out headers));
        Assert.AreEqual("10", Header(headers, "Retry-After"));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.IsTrue(Denied(bucket, Req(), "10.0.0.1", out headers));
        Assert.AreEqual("6", Header(headers, "Retry-After"));
    }

    [TestMethod]
    public void EmitHeaders_AddsTheRateLimitFields_ToAFixedWindowDenial()
    {
        var options = Options(Rate(b => { b.Limit = 2; b.WindowSeconds = 10; b.EmitHeaders = true; }));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.IsTrue(Denied(options, Req(), "10.0.0.1", out var headers));
        Assert.AreEqual("6", Header(headers, "Retry-After"));
        Assert.AreEqual("2", Header(headers, "RateLimit-Limit"));
        Assert.AreEqual("0", Header(headers, "RateLimit-Remaining"));
        Assert.AreEqual("6", Header(headers, "RateLimit-Reset"));
    }

    [TestMethod]
    public void EmitHeaders_ReportsTheBucketState_OnATokenBucketDenial()
    {
        var options = Options(Rate(b => { b.Algorithm = "tokenBucket"; b.Limit = 2; b.WindowSeconds = 10; b.Burst = 3; b.EmitHeaders = true; }));
        for (var i = 0; i < 3; i++)
        {
            Assert.IsFalse(Denied(options, Req(), "10.0.0.1"));
        }

        Assert.IsTrue(Denied(options, Req(), "10.0.0.1", out var headers));
        Assert.AreEqual("5", Header(headers, "Retry-After"));
        Assert.AreEqual("2", Header(headers, "RateLimit-Limit"));
        Assert.AreEqual("0", Header(headers, "RateLimit-Remaining"));
        Assert.AreEqual("15", Header(headers, "RateLimit-Reset"), "three tokens take 15 s to come back");
    }

    [TestMethod]
    public void TwoOptionsInstances_DoNotShareRateCounters()
    {
        var first = Options(Rate(b => { b.Limit = 1; b.WindowSeconds = 60; }));
        var second = Options(Rate(b => { b.Limit = 1; b.WindowSeconds = 60; }));

        Assert.IsFalse(Denied(first, Req(), "10.0.0.1"));
        Assert.IsTrue(Denied(first, Req(), "10.0.0.1"), "the first instance spent its allowance");
        Assert.IsFalse(Denied(second, Req(), "10.0.0.1"), "a second proxy in the process starts with its own counters");
        Assert.AreEqual(1, RoutePolicies.RateWindowCount(first));
        Assert.AreEqual(1, RoutePolicies.RateWindowCount(second));

        // A reload builds a new options instance from the same configuration and gets fresh state.
        var reloaded = Options(Rate(b => { b.Limit = 1; b.WindowSeconds = 60; }));
        Assert.AreEqual(0, RoutePolicies.RateWindowCount(reloaded));
        Assert.IsFalse(Denied(reloaded, Req(), "10.0.0.1"));
    }

    [TestMethod]
    public void MaxKeys_SharesOneOverflowCounter_OnceFullOfLiveKeys()
    {
        var options = Options(Rate(b => { b.Key = "header:X-Id"; b.Limit = 1; b.MaxKeys = 1000; b.WindowSeconds = 60; }));
        for (var i = 0; i < 1000; i++)
        {
            Assert.IsFalse(Denied(options, Req(("X-Id", "k" + i)), "10.0.0.1"));
        }

        Assert.IsFalse(Denied(options, Req(("X-Id", "new-a")), "10.0.0.1"), "the first new key spends the overflow counter");
        Assert.IsTrue(Denied(options, Req(("X-Id", "new-b")), "10.0.0.1"), "later new keys share it");
        Assert.IsTrue(Denied(options, Req(("X-Id", "k5")), "10.0.0.1"), "a known key keeps its own counter");
        Assert.IsTrue(RoutePolicies.RateWindowCount(options) <= 1001, "the cap holds");
    }

    [TestMethod]
    public void MaxKeys_EvictsExpiredCountersBeforeOverflowing()
    {
        var options = Options(Rate(b => { b.Key = "header:X-Id"; b.Limit = 1; b.MaxKeys = 1000; b.WindowSeconds = 10; }));
        for (var i = 0; i < 1000; i++)
        {
            Assert.IsFalse(Denied(options, Req(("X-Id", "k" + i)), "10.0.0.1"));
        }

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.IsFalse(Denied(options, Req(("X-Id", "fresh-a")), "10.0.0.1"));
        Assert.IsTrue(Denied(options, Req(("X-Id", "fresh-a")), "10.0.0.1"), "a fresh key got its own counter, not the shared one");
        Assert.IsFalse(Denied(options, Req(("X-Id", "fresh-b")), "10.0.0.1"));
        Assert.IsTrue(RoutePolicies.RateWindowCount(options) < 1000);
    }

    [TestMethod]
    public void MaxKeys_AppliesPerPolicy()
    {
        var full = Rate(b => { b.Key = "header:X-Id"; b.Limit = 1; b.MaxKeys = 1000; b.WindowSeconds = 60; });
        var other = new RoutePolicyConfig { Name = "other", Kind = "rateLimit", Limit = 1, WindowSeconds = 60, Key = "header:X-Id" };
        var options = new ReverseProxyOptions
        {
            Policies = [full, other],
            Routes =
            [
                new RouteConfig { Id = "a", ClusterId = "c", Policies = ["rate"], Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix } },
                new RouteConfig { Id = "b", ClusterId = "c", Policies = ["other"], Match = new RouteMatch { Path = "/b", PathKind = PathMatchKind.Prefix } },
            ],
        };
        for (var i = 0; i < 1001; i++)
        {
            var request = Req(("X-Id", "k" + i));
            request.RequestUriString8 = (ByteString)"/a";
            Denied(options, request, "10.0.0.1");
        }

        var onB = Req(("X-Id", "k0"));
        onB.RequestUriString8 = (ByteString)"/b";
        Assert.IsFalse(Denied(options, onB, "10.0.0.1"), "the other policy is not capped by the first one's keys");
    }

    [TestMethod]
    public void DefaultPolicy_KeepsTheFixedWindowAndTheClientKey()
    {
        var options = Options(new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 2, WindowSeconds = 10 });
        Assert.IsFalse(Denied(options, Req(("X-Forwarded-For", "1.1.1.1")), "10.0.0.1", Trusted));
        Assert.IsFalse(Denied(options, Req(("X-Forwarded-For", "2.2.2.2")), "10.0.0.1", Trusted), "client key ignores X-Forwarded-For");
        Assert.IsTrue(Denied(options, Req(("X-Forwarded-For", "3.3.3.3")), "10.0.0.1", out var headers, Trusted));
        Assert.AreEqual("10", Header(headers, "Retry-After"));
        Assert.AreEqual(1, headers!.Count);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsFalse(Denied(options, Req(), "10.0.0.1"), "the window restarts after its length, not on a sliding basis");
    }

    [TestMethod]
    public void SessionPath_Denial_CarriesRetryAfter_AndUsesTheServersTrustedProxies()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ForwardedHeaders = new ForwardedHeadersConfig { TrustedProxies = ["127.0.0.0/8"] };
        var policy = Rate(b => { b.Key = "forwardedClient"; b.Limit = 1; b.WindowSeconds = 30; b.EmitHeaders = true; });
        proxy.ReverseProxy = Options(policy);
        var route = proxy.ReverseProxy.Routes![0];

        var first = Session(proxy, "203.0.113.7");
        Assert.IsFalse(RoutePolicies.TryDeny(proxy, first, route));

        var second = Session(proxy, "203.0.113.7");
        Assert.IsTrue(RoutePolicies.TryDeny(proxy, second, route));
        var response = second.HttpClient.Response;
        Assert.AreEqual(429, response.StatusCode);
        Assert.AreEqual("30", response.Headers.GetFirstHeader("Retry-After")!.Value);
        Assert.AreEqual("1", response.Headers.GetFirstHeader("RateLimit-Limit")!.Value);
        Assert.AreEqual("0", response.Headers.GetFirstHeader("RateLimit-Remaining")!.Value);

        var other = Session(proxy, "203.0.113.8");
        Assert.IsFalse(RoutePolicies.TryDeny(proxy, other, route), "another forwarded client has its own counter");
    }

    private static SessionEventArgs Session(ProxyServer proxy, string forwardedFor)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, stream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://example.test/";
        session.HttpClient.Request.Headers.AddHeader("X-Forwarded-For", forwardedFor);
        return session;
    }

    [TestMethod]
    public void HashValue_IsSixteenBytesOfHex_AndStable()
    {
        var a = RateLimiter.HashValue("secret-token");
        Assert.AreEqual(32, a.Length);
        Assert.AreEqual(a, RateLimiter.HashValue("secret-token"));
        Assert.AreNotEqual(a, RateLimiter.HashValue("secret-token2"));
        Assert.IsFalse(a.Contains("secret", StringComparison.Ordinal));
        Assert.AreEqual(32, RateLimiter.HashValue(new string('x', 5000)).Length);
    }
}
