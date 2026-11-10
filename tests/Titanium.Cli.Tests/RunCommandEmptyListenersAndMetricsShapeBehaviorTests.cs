using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;

namespace Titanium.Cli.Tests;

/// <summary>
///     Remaining RunCommand / MetricsListener edges that Sonar still marks thin on the coverage PR: empty-listener
///     fallback bind, service logging minimum-level clamp, unsupported instrument shapes, and Windows sighup no-op.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandEmptyListenersAndMetricsShapeBehaviorTests
{
    // Bug class: a config with listeners: [] never binds the documented 127.0.0.1:8000 fallback, so operators get a
    // process that starts cleanly and accepts nothing.
    [TestMethod]
    [Timeout(90_000)]
    public async Task EmptyListeners_BindsLoopback8000Fallback_AndStopsCleanly()
    {
        if (await PortInUseAsync(8000))
        {
            Assert.Inconclusive("port 8000 is already in use on this machine");
        }

        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-empty-listeners-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, """
            schemaVersion: "7.1"
            listeners: []
            server:
              drainTimeoutSeconds: 0
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
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(35),
                () => stdout + Environment.NewLine + stderr);

            Assert.IsTrue(await CanConnectAsync(8000), "empty listeners must bind the 8000 fallback");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)));
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

    // Bug class: service-mode defaults leave MinimumLevel at Warning, so Information operational lines never reach the
    // Windows file sink that ApplyServiceLoggingDefaults just enabled.
    [TestMethod]
    public void ApplyServiceLoggingDefaults_ClampsWarningMinimumToInformation()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.Logging.Enabled = false;
        proxy.Logging.EnableFile = false;
        proxy.Logging.MinimumLevel = LogLevel.Warning;

        RunCommand.ApplyServiceLoggingDefaults(proxy, logging: null);

        Assert.IsTrue(proxy.Logging.Enabled);
        Assert.AreEqual(LogLevel.Information, proxy.Logging.MinimumLevel);
    }

    // Bug class: Counter&lt;byte&gt; / unsupported value types crash TryCreate or are silently accepted into /metrics.
    [TestMethod]
    public void MetricsListener_UnsupportedValueType_IsRejected()
    {
        var meterName = "twp-cli-shape-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var listener = new MeterListener();
        Instrument? byteCounter = null;
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == "twp.byte.counter")
            {
                byteCounter = instrument;
            }
        };
        listener.Start();

        _ = meter.CreateCounter<byte>("twp.byte.counter");
        Assert.IsNotNull(byteCounter);
        Assert.IsFalse(
            MetricsListener.InstrumentState.TryCreate(byteCounter!, new MetricsListener(), out _),
            "byte counters must be rejected, not crash");
    }

    // Bug class: TrySendSighup on Windows returns true / throws instead of a clean false.
    [TestMethod]
    public void ConfigReloadGate_Windows_SighupIsNoOp_AndDeadPidIsNotAlive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows sighup no-op.");
        }

        Assert.IsFalse(ConfigReloadGate.TrySendSighup(Environment.ProcessId));
        Assert.IsFalse(ConfigReloadGate.IsProcessAlive(int.MaxValue - 7));
    }

    private static async Task<bool> PortInUseAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(200));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
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
            // Best-effort.
        }
    }
}
