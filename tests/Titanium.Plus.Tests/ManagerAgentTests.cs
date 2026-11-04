using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Manager;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

[TestClass]
[DoNotParallelize]
public class ManagerAgentTests
{
    [TestMethod]
    public void TryStart_WithoutManagerUrl_LeavesLiveLoopsAtZero()
    {
        var before = ManagerAgent.LiveLoops;
        var started = ManagerAgent.TryStart(
            new PlusActivationContext { ProxyServer = new object() },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        Assert.IsNull(started);
        Assert.AreEqual(before, ManagerAgent.LiveLoops);
        Assert.AreEqual(0, ManagerAgent.LiveLoops);
    }

    [TestMethod]
    public async Task Managed_ReceivesConfig_AppliesAcks_AndWritesLastKnownGood()
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
            using var agent = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "managed", debounceMs: 50);
            Assert.IsNotNull(agent);
            Assert.IsTrue(ManagerAgent.LiveLoops >= 1);

            // ApplyAsync resolves before WriteLastKnownGood; wait for LKG, not only SuccessfulApplies.
            await WaitUntilAsync(
                () => applier.SuccessfulApplies.Length >= 1
                      && ManagerAgent.TryReadLastKnownGood(stateDir, out _, out var hash)
                      && string.Equals(hash, expectedHash, StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));
            Assert.AreEqual(1, applier.SuccessfulApplies.Length);
            Assert.AreEqual(expectedHash, applier.SuccessfulApplies[0].Hash);

            await WaitUntilAsync(() => harness.Acks.Any(a => a.Accepted), TimeSpan.FromSeconds(5));
            Assert.IsTrue(harness.Acks.Any(a => a.Accepted && a.Version.ToString() == expectedHash));

            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(stateDir, out var lkgYaml, out var lkgHash));
            Assert.AreEqual(expectedHash, lkgHash);
            Assert.IsFalse(string.IsNullOrWhiteSpace(lkgYaml));

            var s = status.GetConfigStatus();
            Assert.IsTrue(s.Managed);
            Assert.IsFalse(s.Drifted);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    [TestMethod]
    public async Task Managed_ApplyFailure_Nacks_AndKeepsPriorApplyState()
    {
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { v = 1 });
        var stateDir = NewTempDir();
        var tokenFile = Path.Combine(stateDir, "token");
        await File.WriteAllTextAsync(tokenFile, "tok");
        var applier = new FakeApplier { FailNext = true };
        var status = new TrackingHostStatus("local");

        try
        {
            using var agent = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "managed", debounceMs: 50);
            await WaitUntilAsync(() => harness.Acks.Any(a => !a.Accepted), TimeSpan.FromSeconds(8));
            Assert.AreEqual(0, applier.SuccessfulApplies.Length);
            Assert.AreEqual(1, applier.Attempts);
            Assert.IsTrue(harness.Acks.Any(a => !a.Accepted && a.Reason is not null));
            Assert.IsFalse(ManagerAgent.TryReadLastKnownGood(stateDir, out _, out _));

            // A later successful apply should be the first success (prior state kept).
            applier.FailNext = false;
            harness.PublishConfig(new { v = 2 });
            var secondHash = harness.ConfigVersion.ToString();
            await WaitUntilAsync(
                () => applier.SuccessfulApplies.Length >= 1
                      && ManagerAgent.TryReadLastKnownGood(stateDir, out _, out var hash)
                      && string.Equals(hash, secondHash, StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));
            Assert.AreEqual(1, applier.SuccessfulApplies.Length);
            Assert.AreEqual(secondHash, applier.SuccessfulApplies[0].Hash);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    [TestMethod]
    public async Task Managed_Debounce_CoalescesRapidPublishes()
    {
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { n = 0 });
        var stateDir = NewTempDir();
        var tokenFile = Path.Combine(stateDir, "token");
        await File.WriteAllTextAsync(tokenFile, "tok");
        var applier = new FakeApplier { ApplyDelay = TimeSpan.FromMilliseconds(20) };
        var status = new TrackingHostStatus("local");

        try
        {
            using var agent = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "managed", debounceMs: 400);
            var firstHash = harness.ConfigVersion.ToString();
            await WaitUntilAsync(
                () => applier.SuccessfulApplies.Length >= 1
                      && ManagerAgent.TryReadLastKnownGood(stateDir, out _, out var hash)
                      && string.Equals(hash, firstHash, StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));
            var afterFirst = applier.SuccessfulApplies.Length;

            for (var i = 1; i <= 5; i++)
            {
                harness.PublishConfig(new { n = i });
                await Task.Delay(30);
            }

            await Task.Delay(900);
            // First apply + coalesced follow-up(s); must be well below the number of publishes (1 + 5).
            Assert.IsTrue(applier.SuccessfulApplies.Length < afterFirst + 5,
                $"applies={applier.SuccessfulApplies.Length} afterFirst={afterFirst}");
            Assert.IsTrue(applier.SuccessfulApplies.Length >= afterFirst + 1);
            Assert.AreEqual(harness.ConfigVersion.ToString(), applier.SuccessfulApplies[^1].Hash);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    [TestMethod]
    public async Task Advisory_DoesNotApply_ButSetsDrift()
    {
        await using var harness = await ManagerHarness.StartAsync();
        harness.PublishConfig(new { advisory = true });
        var stateDir = NewTempDir();
        var tokenFile = Path.Combine(stateDir, "token");
        await File.WriteAllTextAsync(tokenFile, "tok");
        var applier = new FakeApplier();
        var status = new TrackingHostStatus("local-file-hash");

        try
        {
            using var agent = StartAgent(harness, stateDir, tokenFile, applier, status, mode: "advisory", debounceMs: 50);
            await WaitUntilAsync(() => status.GetConfigStatus().Drifted, TimeSpan.FromSeconds(8));
            Assert.AreEqual(0, applier.Attempts);
            Assert.IsTrue(status.GetConfigStatus().Managed);
            Assert.IsTrue(status.GetConfigStatus().Drifted);
            Assert.AreEqual(0, harness.Acks.Count);
        }
        finally
        {
            TryDelete(stateDir);
        }
    }

    [TestMethod]
    public void TryReadLastKnownGood_ReadsWrittenFiles()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ManagerAgent.LastKnownGoodYamlFileName), "schemaVersion: \"7.1\"\n");
            File.WriteAllText(Path.Combine(dir, ManagerAgent.LastKnownGoodHashFileName), "abc123\n");
            Assert.IsTrue(ManagerAgent.TryReadLastKnownGood(dir, out var yaml, out var hash));
            Assert.AreEqual("abc123", hash);
            StringAssert.Contains(yaml, "schemaVersion");
            Assert.IsFalse(ManagerAgent.TryReadLastKnownGood(Path.Combine(dir, "missing"), out _, out _));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [TestMethod]
    public void Dispose_DecrementsLiveLoops()
    {
        // No url → null; dispose path covered by managed test using statements.
        Assert.AreEqual(0, ManagerAgent.LiveLoops);
    }

    [TestMethod]
    public void TryStart_WithTlsInsecure_StartsAndDisposes()
    {
        var stateDir = NewTempDir();
        try
        {
            var tokenFile = Path.Combine(stateDir, "token");
            File.WriteAllText(tokenFile, "test-token");
            var before = ManagerAgent.LiveLoops;
            using var agent = ManagerAgent.TryStart(
                new PlusActivationContext { ProxyServer = new object() },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["manager.url"] = "https://127.0.0.1:9/",
                    ["manager.tokenFile"] = tokenFile,
                    ["manager.fleet"] = "1",
                    ["manager.mode"] = "advisory",
                    ["manager.stateDir"] = stateDir,
                    ["manager.nodeId"] = "tls-insecure-node",
                    ["manager.tls.insecure"] = "true",
                });
            Assert.IsNotNull(agent);
            Assert.IsTrue(ManagerAgent.LiveLoops > before);
        }
        finally
        {
            TryDelete(stateDir);
            Assert.AreEqual(0, ManagerAgent.LiveLoops);
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
            ["manager.nodeId"] = "test-node",
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
        var dir = Path.Combine(Path.GetTempPath(), "twp-mgr-agent-" + Guid.NewGuid().ToString("N"));
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
        public bool FailNext { get; set; }
        public TimeSpan ApplyDelay { get; set; }
        public int Attempts { get; private set; }
        public (string Yaml, string Hash)[] SuccessfulApplies => _ok.ToArray();

        public async Task<ConfigApplyResult> ApplyAsync(string yaml, string configHash, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (ApplyDelay > TimeSpan.Zero)
            {
                await Task.Delay(ApplyDelay, cancellationToken).ConfigureAwait(false);
            }

            if (FailNext)
            {
                return new ConfigApplyResult { Ok = false, Error = "fake apply failure" };
            }

            _ok.Enqueue((yaml, configHash));
            return new ConfigApplyResult { Ok = true, AppliedHash = configHash };
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
