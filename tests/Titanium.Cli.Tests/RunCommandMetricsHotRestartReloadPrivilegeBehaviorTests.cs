using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Cli.Service;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     Remaining Cli gaps that Sonar still marks thin: PrivilegePrompt on Windows, metrics label overflow including
///     <c>result</c>, hot-restart deferred bind failures, Windows reload without a pid file, and a live run that
///     serves <c>/metrics</c> with overload sampling turned on.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandMetricsHotRestartReloadPrivilegeBehaviorTests
{
    [TestCleanup]
    public void ResetPrivilegePrompt() => PrivilegePrompt.ResetForTests();

    // Bug class: Windows UAC / elevation copy still talks about sudo, or strips parent-pid / --config badly so the
    // elevated child cannot find the YAML the operator passed.
    [TestMethod]
    public void PrivilegePrompt_WindowsMessagesAndArgHelpers_StayOperatorFacing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows PrivilegePrompt wording and console attach.");
        }

        StringAssert.Contains(PrivilegePrompt.InteractivePromptMessage(), "Windows security prompt");
        StringAssert.Contains(PrivilegePrompt.FallbackMessage(), "Run as Administrator");
        Assert.IsFalse(
            PrivilegePrompt.FallbackMessage().Contains("sudo", StringComparison.OrdinalIgnoreCase),
            PrivilegePrompt.FallbackMessage());

        var stripped = PrivilegePrompt.TakeInternalArgs([
            "service", "install",
            PrivilegePrompt.ParentPidFlag, // dangling: no pid value
            PrivilegePrompt.ParentPidFlag, "not-a-pid",
            PrivilegePrompt.ParentPidFlag, "99",
            "--config", "rel.yaml",
        ]);
        CollectionAssert.AreEqual(
            new[] { "service", "install", PrivilegePrompt.ParentPidFlag, PrivilegePrompt.ParentPidFlag, "not-a-pid", "--config", "rel.yaml" },
            stripped);
        Assert.AreEqual(99u, PrivilegePrompt.ParentPid);

        var abs = PrivilegePrompt.AbsolutizeConfigArgs(["service", "install", "--config", "rel.yaml"]);
        Assert.IsTrue(Path.IsPathRooted(abs[3]), abs[3]);
        StringAssert.EndsWith(abs[3], "rel.yaml");

        PrivilegePrompt.TryAttachParentConsole();
    }

    // Bug class: TITANIUM_NO_ELEVATE=1 is ignored and a headless CI job still pops UAC / waits forever.
    [TestMethod]
    public async Task PrivilegePrompt_NoElevateEnv_SkipsTheInteractivePrompt()
    {
        if (PrivilegePrompt.IsElevated())
        {
            Assert.Inconclusive("already elevated");
        }

        var previous = Environment.GetEnvironmentVariable(PrivilegePrompt.NoElevateEnv);
        try
        {
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, "1");
            PrivilegePrompt.ResetForTests();
            Assert.IsFalse(PrivilegePrompt.CanPromptInteractively());
            Assert.IsNull(await PrivilegePrompt.EnsureOrRelaunchAsync(["service", "status"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, previous);
            PrivilegePrompt.ResetForTests();
        }
    }

    // Bug class: SeriesKey.Equals / GetHashCode / AllOverflow forget the result slot that LabelNames advertises, so
    // scrapes merge hit/miss series or never fold overflow measurements that only differ on result=.
    [TestMethod]
    public void MetricsSeriesKey_ResultSlotParticipatesInEqualityHashAndOverflow()
    {
        Assert.AreEqual(MetricsListener.LabelNames.Length - 1, MetricsListener.SlotOf("result"));

        var hit = default(MetricsListener.SeriesKey);
        for (var slot = 0; slot < MetricsListener.LabelNames.Length; slot++)
        {
            hit.Set(slot, "v" + slot);
        }

        var miss = hit;
        miss.Set(MetricsListener.SlotOf("result"), "miss");
        Assert.IsFalse(hit.Equals(miss));
        Assert.IsFalse(hit.Equals((object)miss));
        Assert.AreNotEqual(hit.GetHashCode(), miss.GetHashCode());

        var overflow = hit.AllOverflow();
        for (var slot = 0; slot < MetricsListener.LabelNames.Length; slot++)
        {
            Assert.AreEqual(MetricsListener.OverflowValue, overflow.Get(slot));
        }
    }

    // Bug class: direction / reason / outcome / result tags are dropped before scrape, so operators cannot tell
    // cache hit from miss (or inbound from outbound) on /metrics.
    [TestMethod]
    public void MetricsListener_DirectionReasonOutcomeResult_AppearOnScrape()
    {
        var meterName = "twp-cli-labels-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        meter.CreateCounter<long>("twp.proxy.label_slots_total").Add(
            1,
            new KeyValuePair<string, object?>("route", "r"),
            new KeyValuePair<string, object?>("cluster", "c"),
            new KeyValuePair<string, object?>("direction", "inbound"),
            new KeyValuePair<string, object?>("reason", "timeout"),
            new KeyValuePair<string, object?>("outcome", "fail"),
            new KeyValuePair<string, object?>("result", "miss"));

        var text = exposition.Render();
        StringAssert.Contains(text, "direction=\"inbound\"");
        StringAssert.Contains(text, "reason=\"timeout\"");
        StringAssert.Contains(text, "outcome=\"fail\"");
        StringAssert.Contains(text, "result=\"miss\"");
    }

    // Bug class: one deferred bind throwing aborts the rest (HTTP/3 stays down forever) or turns into an unobserved
    // task exception that crashes the successor after TCP is already serving.
    [TestMethod]
    [Timeout(15_000)]
    public async Task HotRestartDeferredBind_OneBindThrows_StillRunsTheRestAndReportsFailure()
    {
        var platform = new FakeHotRestartPlatform();
        var bound = 0;
        var errors = new List<string>();

        var ok = await HotRestartDeferredBind.RunAsync(
            previousPid: 4242,
            platform,
            [
                () => throw new InvalidOperationException("udp-port-busy"),
                () => bound++,
            ],
            timeout: TimeSpan.FromSeconds(5),
            poll: TimeSpan.FromMilliseconds(10),
            errors.Add,
            CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, bound, "later binds must still run after an earlier failure");
        Assert.IsTrue(errors.Exists(e => e.Contains("udp-port-busy", StringComparison.Ordinal)));
    }

    // Bug class: a predecessor that never exits makes the successor wait forever instead of keeping TCP up and
    // logging that UDP/HTTP3 stayed off.
    [TestMethod]
    [Timeout(15_000)]
    public async Task HotRestartDeferredBind_PredecessorNeverExits_TimesOutWithoutBinding()
    {
        var platform = new FakeHotRestartPlatform();
        platform.Alive.Add(77);
        var bound = 0;
        var errors = new List<string>();

        var ok = await HotRestartDeferredBind.RunAsync(
            77,
            platform,
            [() => bound++],
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(10),
            errors.Add,
            CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, bound);
        StringAssert.Contains(errors.Single(), "did not exit");
    }

    // Bug class: `titanium reload -c` with a waiter but no pid file crashes, or claims a pid it cannot prove.
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ReloadCommand_WindowsWaiterWithoutPidFile_ReportsSignaledWithoutAPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle path is Windows-only.");
        }

        var configPath = Path.Combine(
            Path.GetTempPath(), "twp-reload-nopid-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(configPath, "schemaVersion: \"7.1\"\n");
        ConfigReloadGate.TryDeletePidFile(configPath);

        var stdout = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(stdout);
            using (var handle = ConfigReloadGate.CreateWindowsReloadEvent(configPath, out _))
            {
                Assert.AreEqual(0, await ReloadCommand.ExecuteAsync(["reload", "-c", configPath]));
                await AsyncConsole.FlushAsync();
                Assert.IsTrue(handle.WaitOne(0));
            }
        }
        finally
        {
            Console.SetOut(previous);
            try
            {
                File.Delete(configPath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }

        var line = stdout.ToString();
        StringAssert.Contains(line, "Reload signaled (Windows event).");
        Assert.IsFalse(line.Contains("pid ", StringComparison.Ordinal), line);
    }

    // Bug class: Apply after dispose restarts the sampler (timer leak), or Enable with active-connection thresholds
    // never samples so shedding stays off forever.
    [TestMethod]
    [Timeout(30_000)]
    public async Task OverloadProtectionHost_DisposeThenApply_IsNoOp_AndActiveConnectionsAreSampled()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var warnings = new List<string>();
        var host = new OverloadProtectionHost(proxy, warnings.Add);
        await host.DisposeAsync();
        host.Apply(new OverloadConfig { Enabled = true, IntervalMs = 100 });
        Assert.IsFalse(host.IsRunning);

        await using var live = new OverloadProtectionHost(proxy, warnings.Add);
        live.Apply(new OverloadConfig
        {
            Enabled = true,
            IntervalMs = 100,
            HeapPercent = new OverloadSignalConfig { Elevated = 0, Critical = 0 },
            ThreadPoolQueue = new OverloadSignalConfig { Elevated = 0, Critical = 0 },
            ActiveConnections = new OverloadSignalConfig { Elevated = 1, Critical = 2 },
        });
        Assert.IsTrue(live.IsRunning);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !proxy.OverloadProtectionEnabled)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(proxy.OverloadProtectionEnabled, "the sampler must enable shedding after the first tick");
    }

    // Bug class: metrics bind succeeds but the scrape path is never wired (or overload start throws and takes the
    // proxy down), so operators get a running process with no /metrics and no shedding.
    [TestMethod]
    [Timeout(90_000)]
    public async Task RunCommand_MetricsAndOverloadEnabled_ServesScrapeAndStopsCleanly()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-metrics-ol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var metricsPort = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              metrics:
                enabled: true
                host: "127.0.0.1"
                port: {metricsPort}
              overload:
                enabled: true
                intervalMs: 200
                heapPercent:
                  elevated: 99
                  critical: 100
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);

            await WaitForAsync(
                async () => await CanConnectAsync(proxyPort) && await MetricsLooksHealthyAsync(metricsPort),
                TimeSpan.FromSeconds(40),
                () => stdout + Environment.NewLine + stderr);

            var scrape = await RawHttpAsync(metricsPort, "GET /metrics HTTP/1.1\r\nHost: x\r\n\r\n");
            StringAssert.StartsWith(scrape, "HTTP/1.1 200 OK");
            StringAssert.Contains(scrape, "twp_");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)), stderr.ToString());
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> MetricsLooksHealthyAsync(int port)
    {
        try
        {
            var response = await RawHttpAsync(port, "GET /metrics HTTP/1.1\r\nHost: x\r\n\r\n");
            return response.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RawHttpAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        var buffer = new byte[64 * 1024];
        var total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token);
            if (read <= 0)
            {
                break;
            }

            total += read;
            var soFar = Encoding.ASCII.GetString(buffer, 0, total);
            var headerEnd = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            var headers = soFar.AsSpan(0, headerEnd);
            const string lengthKey = "Content-Length:";
            var lengthIdx = headers.IndexOf(lengthKey, StringComparison.OrdinalIgnoreCase);
            if (lengthIdx < 0)
            {
                break;
            }

            var line = headers[(lengthIdx + lengthKey.Length)..];
            var lineEnd = line.IndexOf("\r\n");
            if (lineEnd >= 0)
            {
                line = line[..lineEnd];
            }

            if (int.TryParse(line.Trim(), out var contentLength)
                && total >= headerEnd + 4 + contentLength)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, Math.Max(0, total));
    }

    private static async Task WaitForAsync(Func<Task<bool>> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(detail());
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The run may still hold a handle briefly.
        }
    }

    private sealed class FakeHotRestartPlatform : IHotRestartPlatform
    {
        public HashSet<int> Alive { get; } = new();

        public bool IsProcessAlive(int pid) => Alive.Contains(pid);

        public bool IsSameProgram(int pid) => Alive.Contains(pid);

        public bool SignalHandover(int pid) => Alive.Contains(pid);
    }
}
