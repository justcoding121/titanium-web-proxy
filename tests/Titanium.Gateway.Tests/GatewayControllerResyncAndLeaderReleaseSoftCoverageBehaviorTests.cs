using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: GatewayController ResyncInterval Invalidate, follower
///     ReconcileOutcome, LeaderElector Release when lease holder no longer matches, and
///     Program parse exit 2.
/// </summary>
[TestClass]
public class GatewayControllerResyncAndLeaderReleaseSoftCoverageBehaviorTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Controller_ResyncInterval_ForcesOutputRewrite_AndFollowerSkipsPublish()
    {
        await using var fixture = new ClusterFixture();
        var time = new ManualTime();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var options = ClusterFixture.Options(TimeSpan.Zero) with
        {
            ResyncInterval = TimeSpan.FromSeconds(1),
            ConfirmPollInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(50),
        };
        var leader = new LeaderElector(
            fixture.Api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "leader-a", Name = "soft-lease" },
            NullLogger.Instance,
            time);
        var controller = new GatewayController(
            cache,
            new OutputWriter(fixture.Api, options.Output),
            new StatusWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance),
            fixture.Observer,
            leader,
            options,
            NullLogger.Instance,
            time,
            events: new EventWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance));

        using var stop = new CancellationTokenSource();
        var cacheRun = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            await leader.StepAsync(CancellationToken.None);
            Assert.IsTrue(leader.IsLeader);

            var first = await controller.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsTrue(first.Leader);
            Assert.IsTrue(first.WroteOutput);

            time.Advance(TimeSpan.FromSeconds(2));
            var resync = await controller.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsTrue(resync.WroteOutput);

            var follower = new LeaderElector(
                fixture.Api,
                new LeaderElectionOptions { Namespace = "ns", Identity = "follower-b", Name = "soft-lease" },
                NullLogger.Instance,
                time);
            var followerController = new GatewayController(
                cache,
                new OutputWriter(fixture.Api, options.Output),
                new StatusWriter(fixture.Api, ClusterFixture.Controller, NullLogger.Instance),
                fixture.Observer,
                follower,
                options,
                NullLogger.Instance,
                time);
            var skipped = await followerController.ReconcileOnceAsync(CancellationToken.None);
            Assert.IsFalse(skipped.Leader);
            Assert.IsFalse(skipped.WroteOutput);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await cacheRun;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestMethod]
    public async Task LeaderElector_Release_WhenHolderChanged_ReturnsQuietly()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var a = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a", Name = "release-lease" },
            NullLogger.Instance,
            time);
        await a.StepAsync(CancellationToken.None);
        Assert.IsTrue(a.IsLeader);

        var lease = api.Get("leases", "ns", "release-lease")!;
        lease["spec"]!["holderIdentity"] = "other";
        api.Apply("leases", lease);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await a.RunAsync(stop.Token);
        Assert.IsFalse(a.IsLeader);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_MissingDataPlaneService_ExitsTwo()
    {
        var testAsm = Path.GetDirectoryName(typeof(GatewayControllerResyncAndLeaderReleaseSoftCoverageBehaviorTests).Assembly.Location)!;
        string? dll = null;
        for (var dir = new DirectoryInfo(testAsm); dir is not null && dll is null; dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "Titanium.Gateway", "bin", config, "net10.0", "titanium-gateway.dll");
                if (File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".deps.json")))
                {
                    dll = candidate;
                    break;
                }
            }
        }

        Assert.IsNotNull(dll);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { dll!, "--no-leader-election" },
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["POD_NAMESPACE"] = "gw";
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(cts.Token);
        var stderr = await stderrTask;
        Assert.AreEqual(2, process.ExitCode, stderr);
        StringAssert.Contains(stderr, "titanium-gateway:");
    }
}
