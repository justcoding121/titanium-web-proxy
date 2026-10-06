using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class HeaderLimitEnforcementTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(60_000)]
    public async Task TooManyRequestHeaders_Returns431()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var reached = false;
        server.HandleRequest(context =>
        {
            reached = true;
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        SetHeaderBounds(proxy, maxCount: 4, maxAggregate: 256 * 1024);
        var client = testSuite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpUrl);
        for (var i = 0; i < 12; i++)
            request.Headers.TryAddWithoutValidation("X-Extra-" + i, "v");

        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestHeaderFieldsTooLarge, response.StatusCode);
        Assert.IsFalse(reached);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TooManyOriginResponseHeaders_Returns502_ObserveAndRaisedLimitPass()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            for (var i = 0; i < 12; i++)
                context.Response.Headers["X-Origin-" + i] = "v";
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        SetHeaderBounds(proxy, maxCount: 6, maxAggregate: 256 * 1024);
        var client = testSuite.GetClient(proxy);
        using var rejected = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.BadGateway, rejected.StatusCode);

        proxy.PolicyModes = proxy.PolicyModes.With(PolicyFamily.HeaderLimits, PolicyMode.Observe);
        using var observed = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.OK, observed.StatusCode);

        proxy.PolicyModes = proxy.PolicyModes.With(PolicyFamily.HeaderLimits, PolicyMode.Enforce);
        SetHeaderBounds(proxy, maxCount: 64, maxAggregate: 256 * 1024);
        using var raised = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.OK, raised.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task KeepAlive_TwoRequests_UnderTheLimit_BothSucceed()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));
        var proxy = testSuite.GetProxy();
        SetHeaderBounds(proxy, maxCount: 64, maxAggregate: 256 * 1024);
        var client = testSuite.GetClient(proxy);

        using var first = await client.GetAsync(server.ListeningHttpUrl);
        using var second = await client.GetAsync(server.ListeningHttpUrl);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
    }

    private static void SetHeaderBounds(ProxyServer proxy, int maxCount, long maxAggregate)
    {
        var current = proxy.ResourceLimits;
        proxy.ResourceLimits = ProxyResourceLimits.Create(
                current.MaxHeaderLineBytes,
                maxCount,
                maxAggregate,
                current.MaxEncodedBodyBytes,
                current.MaxDecodedBodyBytes,
                current.MaxDecompressionRatio,
                current.MaxConcurrentClients,
                current.MaxConcurrentStreamsPerConnection,
                current.MaxPeerInitiatedIncompleteStreamResets,
                current.MaxOpenHeaderBlockFrames,
                current.MaxOpenHeaderBlockDuration,
                current.ConnectionPoolingEnabled,
                current.MaxCachedConnectionsPerHost,
                current.MaxCertificateCacheEntries)
            .WithCertificateCacheBounds(current.MaxCertificateCacheEntries, current.MaxCertificateDiskCacheEntries)
            .WithMaxOriginHttp2ConnectionsPerAuthority(current.MaxOriginHttp2ConnectionsPerAuthority)
            .WithMaxHttp3FramePayloadBytes(current.MaxHttp3FramePayloadBytes)
            .WithMaxDeferredOutboundBytesPerStream(current.MaxDeferredOutboundBytesPerStream)
            .WithTrailerHeaderBounds(current.MaxTrailerHeaderCount, current.MaxTrailerHeaderBlockBytes)
            .WithMaxHttp2CompressedHeaderBlockBytes(current.MaxHttp2CompressedHeaderBlockBytes)
            .WithMaxInterimResponses(current.MaxInterimResponses)
            .WithAuthenticationBounds(
                current.MaxAuthChallengeRounds,
                current.MaxUpstreamProxyAuthenticationAttempts,
                current.MaxWinAuthTokenBytes)
            .WithHttp2WindowUpdateTimeoutSeconds(current.Http2WindowUpdateTimeoutSeconds);
    }
}
