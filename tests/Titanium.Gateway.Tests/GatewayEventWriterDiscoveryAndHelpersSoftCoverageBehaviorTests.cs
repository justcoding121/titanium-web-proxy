using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: EventWriter patch/post failure throws (caught by Apply),
///     ClusterCache discovery exception fallback, Hostnames empty-string intersect, DurationParser.
/// </summary>
[TestClass]
public class GatewayEventWriterDiscoveryAndHelpersSoftCoverageBehaviorTests
{
    private sealed class StatusApi : IKubeApi
    {
        public HttpStatusCode PatchStatus { get; set; } = HttpStatusCode.InternalServerError;
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.InternalServerError;

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Patch)
            {
                return Task.FromResult(new KubeResponse(PatchStatus, "patch-failed"));
            }

            if (method == HttpMethod.Post)
            {
                return Task.FromResult(new KubeResponse(PostStatus, "post-failed"));
            }

            return Task.FromResult(new KubeResponse(HttpStatusCode.OK, "{}"));
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FlakyDiscoveryApi : IKubeApi
    {
        private readonly IKubeApi _inner;
        private int _remainingThrows;

        public FlakyDiscoveryApi(IKubeApi inner, int remainingThrows)
        {
            _inner = inner;
            _remainingThrows = remainingThrows;
        }

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (pathAndQuery.StartsWith("/apis/gateway.networking.k8s.io", StringComparison.Ordinal)
                && Interlocked.Decrement(ref _remainingThrows) >= 0)
            {
                throw new HttpRequestException("discovery transport failed");
            }

            return _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task EventWriter_PatchFailureAndPostFailure_AreCaughtByApply()
    {
        var time = new ManualTime();
        var item = new IngressEvent("default", "ing", "uid-1", "Warning", "Rejected", "bad rule");

        // Post failure (line 134) — Apply catches KubeApiException.
        var postFail = new StatusApi { PostStatus = HttpStatusCode.InternalServerError };
        var postWriter = new EventWriter(postFail, "titanium.io/gateway", NullLogger.Instance, time)
        {
            RepeatAfter = TimeSpan.FromMinutes(1),
        };
        Assert.AreEqual(1, await postWriter.ApplyAsync([item], CancellationToken.None));

        // Succeed once, then bump with a non-404 patch failure (line 94).
        var bumpApi = new StatusApi { PostStatus = HttpStatusCode.Created, PatchStatus = HttpStatusCode.Forbidden };
        var bumper = new EventWriter(bumpApi, "titanium.io/gateway", NullLogger.Instance, time)
        {
            RepeatAfter = TimeSpan.FromMinutes(1),
        };
        Assert.AreEqual(1, await bumper.ApplyAsync([item], CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, await bumper.ApplyAsync([item], CancellationToken.None));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_DiscoveryTransportFailure_FallsBackToDefaults()
    {
        await using var fixture = new ClusterFixture();
        var api = new FlakyDiscoveryApi(fixture.Api, remainingThrows: 1);
        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(api, new ClusterCacheOptions(), NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            Assert.IsTrue(cache.HasSynced);
            _ = cache.Snapshot();
        }
        finally
        {
            stop.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public void HostnamesAndDuration_EmptyAndMsArms()
    {
        Assert.IsTrue(Hostnames.TryIntersect("", "App.Example", out var fromEmpty));
        Assert.AreEqual("app.example", fromEmpty);
        Assert.IsTrue(Hostnames.TryIntersect("App.Example", "", out var toEmpty));
        Assert.AreEqual("app.example", toEmpty);
        Assert.IsNull(Hostnames.Normalize("*"));
        Assert.AreEqual(2, Hostnames.Rank(null));

        Assert.IsTrue(DurationParser.TryParse("500ms", out var ms));
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), ms);
        Assert.IsTrue(DurationParser.TryParse("1h", out var hour));
        Assert.AreEqual(TimeSpan.FromHours(1), hour);
        Assert.IsFalse(DurationParser.TryParse("not-a-duration", out _));
    }

    [TestMethod]
    public void TcpRoute_SecondOwner_IsRejected()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        b.TcpRoutes.Add(TcpRoute("first", "default", Backend("web")));
        b.TcpRoutes.Add(TcpRoute("second", "default", Backend("web")));
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);

        Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);
        RouteParentStatus Parent(string route) =>
            result.StatusPatches.Single(p => p.Kind == StatusKind.TcpRoute && p.Name == route).Parents!.Single();

        Assert.AreEqual("True", Cond(Parent("first").Conditions, "Accepted").Status);
        Assert.AreEqual("False", Cond(Parent("second").Conditions, "Accepted").Status);
        Assert.AreEqual("NotAllowedByListeners", Cond(Parent("second").Conditions, "Accepted").Reason);
    }
}
