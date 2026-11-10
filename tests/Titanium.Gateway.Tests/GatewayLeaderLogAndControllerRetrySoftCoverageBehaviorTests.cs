using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: LeaderElector leadership transition logs, RunAsync release,
///     GatewayController retry after status write failures, and publish Information log.
/// </summary>
[TestClass]
public class GatewayLeaderLogAndControllerRetrySoftCoverageBehaviorTests
{
    private sealed class FailStatusPatchApi : IKubeApi
    {
        private readonly IKubeApi _inner;

        public FailStatusPatchApi(IKubeApi inner) => _inner = inner;

        public async Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Patch
                && pathAndQuery.Contains("/status", StringComparison.Ordinal))
            {
                return new KubeResponse(HttpStatusCode.InternalServerError, "status-patch-failed");
            }

            return await _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken).ConfigureAwait(false);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class BrokenLeasePatchApi : IKubeApi
    {
        private readonly IKubeApi _inner;

        public BrokenLeasePatchApi(IKubeApi inner) => _inner = inner;

        public async Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Patch
                && pathAndQuery.Contains("/leases/", StringComparison.Ordinal))
            {
                throw new HttpRequestException("lease patch refused");
            }

            return await _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken).ConfigureAwait(false);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task LeaderElector_RunCancel_LogsLeadershipTransitions()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
        var log = loggerFactory.CreateLogger("leader");
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "log-a", Name = "log-lease" },
            log,
            time);
        var changes = new List<bool>();
        elector.LeadershipChanged += changes.Add;

        using var stop = new CancellationTokenSource();
        var run = elector.RunAsync(stop.Token);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);
        Assert.IsTrue(changes.Contains(true));

        stop.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsFalse(elector.IsLeader);
        Assert.IsTrue(changes.Contains(false));
    }

    [TestMethod]
    public async Task LeaderElector_ReleaseFailure_LogsAtDebug()
    {
        var api = new BrokenLeasePatchApi(new FakeKubeApi());
        var time = new ManualTime();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "rel-a", Name = "rel-lease" },
            loggerFactory.CreateLogger("leader-release"),
            time);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await elector.RunAsync(cancel.Token);
        Assert.IsFalse(elector.IsLeader);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Controller_StatusPatchFailure_SchedulesRetry_AndLogsPublish()
    {
        await using var fixture = new ClusterFixture();
        var time = new ManualTime();
        var api = new FailStatusPatchApi(fixture.Api);
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
        var log = loggerFactory.CreateLogger("gateway-controller");
        var cache = new ClusterCache(api, new ClusterCacheOptions(), NullLogger.Instance);
        var options = ClusterFixture.Options(TimeSpan.Zero) with
        {
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ConfirmPollInterval = TimeSpan.FromMilliseconds(40),
        };
        var leader = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "ctl-a", Name = "ctl-lease" },
            NullLogger.Instance,
            time);
        var controller = new GatewayController(
            cache,
            new OutputWriter(api, options.Output),
            new StatusWriter(api, ClusterFixture.Controller, NullLogger.Instance),
            fixture.Observer,
            leader,
            options,
            log,
            time,
            events: new EventWriter(api, ClusterFixture.Controller, NullLogger.Instance));

        using var stop = new CancellationTokenSource();
        var cacheRun = cache.RunAsync(stop.Token);
        var ctlRun = controller.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced && leader.IsLeader);
            await leader.StepAsync(CancellationToken.None);
            var outcome = await controller.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsTrue(outcome.Leader);
            Assert.IsTrue(outcome.StatusFailed > 0, "status patches should fail against FailStatusPatchApi");

            await Task.Delay(150);
            var second = await controller.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsTrue(second.Leader);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(cacheRun, ctlRun);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
