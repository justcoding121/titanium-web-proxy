using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Manager;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

/// <summary>
/// Chaos rows for the manager agent: manager flapping (keep LKG, no apply churn) and agent dispose/restart
/// (LiveLoops returns to 0).
/// </summary>
[TestClass]
[DoNotParallelize]
public class ManagerAgentChaosTests
{
    [TestMethod]
    public async Task ManagerFlapping_DoesNotChurnApplies_AndKeepsLastKnownGood()
    {
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { schemaVersion = "7.1", listeners = Array.Empty<object>() });
        var expectedHash = harness.ConfigVersion.ToString();

        var stateDir = NewTempDir();
        var tokenFile = Path.Combine(stateDir, "token");
        await File.WriteAllTextAsync(tokenFile, "test-token");
        var applier = new FakeApplier();
        var status = new TrackingHostStatus("local-hash");

        try
        {
            using var agent = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "managed", debounceMs: 200);
            Assert.IsNotNull(agent);

            await WaitUntilAsync(() => applier.SuccessfulApplies.Length >= 1, TimeSpan.FromSeconds(8));
            Assert.AreEqual(1, applier.SuccessfulApplies.Length);
            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(stateDir, out _, out var lkgHash));
            Assert.AreEqual(expectedHash, lkgHash);

            // Manager goes away: stop the listener. Agent must not re-apply (have hash set) and LKG stays.
            await harness.SuspendAsync();
            var appliesAtDown = applier.SuccessfulApplies.Length;
            await Task.Delay(1500);
            Assert.AreEqual(appliesAtDown, applier.SuccessfulApplies.Length,
                "manager down must not churn applies while LKG is held");
            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(stateDir, out var lkgYaml, out var stillHash));
            Assert.AreEqual(expectedHash, stillHash);
            Assert.IsFalse(string.IsNullOrWhiteSpace(lkgYaml));

            // Manager returns with rapid republishes; debounce must keep apply count well below publish count.
            await harness.ResumeAsync();
            for (var i = 0; i < 6; i++)
            {
                harness.PublishConfig(new { n = i });
                await Task.Delay(40);
            }

            await Task.Delay(900);
            Assert.IsTrue(applier.SuccessfulApplies.Length < appliesAtDown + 6,
                $"applies={applier.SuccessfulApplies.Length} after flap republishes");
            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(stateDir, out _, out var finalHash));
            Assert.AreEqual(harness.ConfigVersion.ToString(), finalHash);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    [TestMethod]
    public async Task AgentFlapping_DisposeRestarts_LiveLoopsReturnsToZero()
    {
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { flap = true });
        var stateDir = NewTempDir();
        var tokenFile = Path.Combine(stateDir, "token");
        await File.WriteAllTextAsync(tokenFile, "tok");
        var applier = new FakeApplier();
        var status = new TrackingHostStatus("local");

        try
        {
            Assert.AreEqual(0, ManagerAgent.LiveLoops);

            var first = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "advisory", debounceMs: 50);
            Assert.IsNotNull(first);
            Assert.IsTrue(ManagerAgent.LiveLoops >= 1);

            first.Dispose();
            await WaitUntilAsync(() => ManagerAgent.LiveLoops == 0, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, ManagerAgent.LiveLoops);

            var second = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "advisory", debounceMs: 50);
            Assert.IsNotNull(second);
            Assert.IsTrue(ManagerAgent.LiveLoops >= 1);
            second.Dispose();
            await WaitUntilAsync(() => ManagerAgent.LiveLoops == 0, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, ManagerAgent.LiveLoops);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    private static ManagerAgent? StartAgent(
        ManagerHarness harness,
        string stateDir,
        string tokenFile,
        FakeApplier applier,
        TrackingHostStatus status,
        string mode,
        int debounceMs)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["manager.url"] = harness.BaseUrl.TrimEnd('/'),
            ["manager.tokenFile"] = tokenFile,
            ["manager.fleet"] = "1",
            ["manager.mode"] = mode,
            ["manager.stateDir"] = stateDir,
            ["manager.nodeId"] = "chaos-node",
            ["manager.applyDebounceMs"] = debounceMs.ToString(),
        };
        return ManagerAgent.TryStart(
            new PlusActivationContext
            {
                ProxyServer = new object(),
                Options = options,
                HostStatus = status,
                ConfigApplier = applier,
                StateDirectory = stateDir,
            },
            options);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(40);
        }

        Assert.Fail("Condition not met within " + timeout);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-mgr-chaos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed class FakeApplier : IConfigApplier
    {
        private readonly ConcurrentQueue<(string Yaml, string Hash)> _ok = new();
        public int Attempts { get; private set; }
        public (string Yaml, string Hash)[] SuccessfulApplies => _ok.ToArray();

        public Task<ConfigApplyResult> ApplyAsync(string yaml, string configHash, CancellationToken cancellationToken = default)
        {
            Attempts++;
            _ok.Enqueue((yaml, configHash));
            return Task.FromResult(new ConfigApplyResult { Ok = true, AppliedHash = configHash });
        }
    }

    private sealed class TrackingHostStatus : IPlusHostStatus
    {
        private readonly object _gate = new();
        private bool _managed;
        private bool _drifted;
        private readonly string _configHash;

        public TrackingHostStatus(string configHash) => _configHash = configHash;

        public void SetManagedState(bool managed, bool drifted)
        {
            lock (_gate)
            {
                _managed = managed;
                _drifted = drifted;
            }
        }

        public PlusConfigStatus GetConfigStatus()
        {
            lock (_gate)
            {
                return new PlusConfigStatus
                {
                    ConfigHash = _configHash,
                    Managed = _managed,
                    Drifted = _drifted,
                };
            }
        }

        public IReadOnlyList<PlusCertificateInfo> GetCertificates() => [];
    }
}
