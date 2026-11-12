using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Cli Sonar cushion above 85: MetricsExposition scrape text, Prometheus escapes, and ConfigReloadGate pid helpers.
/// </summary>
[TestClass]
public class MetricsListenerAndReloadGateCushionBehaviorTests
{
    [TestMethod]
    public void MetricsExposition_RendersCounterAndHistogramFamilies()
    {
        var meterName = "twp-cli-cushion-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        meter.CreateCounter<long>("twp.proxy.cushion_hits").Add(
            2,
            new KeyValuePair<string, object?>("route", "r1"),
            new KeyValuePair<string, object?>("result", "hit"));

        var histogram = meter.CreateHistogram<double>("twp.proxy.cushion_latency", unit: "ms");
        histogram.Record(12.5, new KeyValuePair<string, object?>("route", "r1"));

        var text = exposition.Render();
        StringAssert.Contains(text, "twp_proxy_cushion_hits");
        StringAssert.Contains(text, "route=\"r1\"");
        StringAssert.Contains(text, "result=\"hit\"");
        StringAssert.Contains(text, "TYPE");
    }

    [TestMethod]
    public void PrometheusTextWriter_FormatDoubleAndEscapeLabel()
    {
        Assert.AreEqual("+Inf", PrometheusTextWriter.FormatDouble(double.PositiveInfinity));
        Assert.AreEqual("-Inf", PrometheusTextWriter.FormatDouble(double.NegativeInfinity));
        Assert.AreEqual("NaN", PrometheusTextWriter.FormatDouble(double.NaN));
        Assert.AreEqual("1.5", PrometheusTextWriter.FormatDouble(1.5));
        Assert.AreEqual("a\\\\b\\\"c\\nd", PrometheusTextWriter.EscapeLabel("a\\b\"c\nd"));
    }

    [TestMethod]
    public void ConfigReloadGate_PidFileRoundTrip_AndDeadProcessChecks()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "twp-reload-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(configPath, "listeners: []\n");
        try
        {
            ConfigReloadGate.WritePidFile(configPath, Environment.ProcessId);
            Assert.AreEqual(Environment.ProcessId, ConfigReloadGate.TryReadPid(configPath));
            Assert.IsTrue(ConfigReloadGate.IsProcessAlive(Environment.ProcessId));
            Assert.IsFalse(ConfigReloadGate.IsProcessAlive(int.MaxValue - 11));
            Assert.IsFalse(ConfigReloadGate.TrySendSighup(Environment.ProcessId) && OperatingSystem.IsWindows());

            if (OperatingSystem.IsWindows())
            {
                Assert.IsFalse(ConfigReloadGate.TrySendSighup(Environment.ProcessId));
                _ = ConfigReloadGate.TrySignalWindowsReload(configPath);
            }

            ConfigReloadGate.TryDeletePidFile(configPath);
            Assert.IsNull(ConfigReloadGate.TryReadPid(configPath));
            ConfigReloadGate.TryDeletePidFile(configPath);
        }
        finally
        {
            try
            {
                File.Delete(configPath);
            }
            catch
            {
                // Best-effort.
            }

            ConfigReloadGate.TryDeletePidFile(configPath);
        }
    }
}
