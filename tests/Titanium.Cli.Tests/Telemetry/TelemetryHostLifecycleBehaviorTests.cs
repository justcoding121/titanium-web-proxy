using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Telemetry;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests.Telemetry;

/// <summary>
/// Lifecycle edges for <see cref="TelemetryHost"/>: inactive configs must not start exporters, flush/dispose must
/// stay best-effort, and metrics-only hosts must still flush without a tracer.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TelemetryHostLifecycleBehaviorTests
{
    // Bug class: an inactive telemetry block still constructs exporters (or throws) instead of returning null.
    [TestMethod]
    public void TryStart_WhenInactive_ReturnsNullAndLeavesTracingOff()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var warnings = new List<string>();

        var host = TelemetryHost.TryStart(
            proxy,
            new TelemetryOptions { TracesEnabled = false, MetricsEnabled = false },
            warnings.Add);

        Assert.IsNull(host);
        Assert.IsFalse(proxy.TracingEnabled);
        Assert.AreEqual(0, warnings.Count);
    }

    // Bug class: ForceFlush / Dispose throw when only metrics (or only traces) are enabled, or when called twice.
    [TestMethod]
    [Timeout(30_000)]
    public void MetricsOnly_ForceFlushAndDoubleDispose_NeverThrow()
    {
        using var receiver = new OtlpReceiver();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig
            {
                ServiceName = "twp-metrics-only",
                Otlp = new OtlpExporterConfig { Endpoint = receiver.Endpoint, Protocol = "http", TimeoutMs = 2000 },
                Traces = new TelemetryTracesConfig { Enabled = false },
                Metrics = new TelemetryMetricsConfig { Enabled = true, IntervalMs = 60_000 },
            },
            _ => null);

        Assert.IsTrue(options.Active);
        Assert.IsFalse(options.TracesEnabled);
        Assert.IsTrue(options.MetricsEnabled);

        var host = TelemetryTestOptions.Start(proxy, options);
        try
        {
            Assert.AreSame(options, host.Options);
            Assert.AreEqual(0, host.QueueDropped);
            Assert.AreEqual(0, host.ExportFailed);
            Assert.IsFalse(proxy.TracingEnabled, "metrics-only must not turn on request tracing");
            Assert.IsTrue(host.ForceFlush());
        }
        finally
        {
            host.Dispose();
            host.Dispose();
        }
    }

    // Bug class: traces-only ForceFlush returns false / throws when the collector is unreachable at flush time.
    [TestMethod]
    [Timeout(30_000)]
    public void TracesOnly_ForceFlush_ReturnsTrueEvenWhenCollectorIsDown()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        // Bind nothing: point at a loopback port that refuses connections.
        var dead = new Uri($"http://127.0.0.1:{GetFreePort()}/v1/traces");
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig
            {
                ServiceName = "twp-flush",
                Otlp = new OtlpExporterConfig { Endpoint = dead.ToString(), Protocol = "http", TimeoutMs = 200 },
                Traces = new TelemetryTracesConfig { Enabled = true, Sampler = "always" },
            },
            name => name == "OTEL_BSP_SCHEDULE_DELAY" ? "50" : null);

        var host = TelemetryTestOptions.Start(proxy, options);
        try
        {
            Assert.IsTrue(proxy.TracingEnabled);
            Assert.IsTrue(host.ForceFlush());
        }
        finally
        {
            host.Dispose();
        }
    }

    // Bug class: Describe picks the wrong endpoint / protocol token for metrics-only or http exporters.
    [TestMethod]
    public void Describe_MetricsOnlyOverHttp_NamesTheMetricsEndpoint()
    {
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig
            {
                ServiceName = "svc",
                Otlp = new OtlpExporterConfig
                {
                    Endpoint = "http://127.0.0.1:4318",
                    Protocol = "http",
                },
                Metrics = new TelemetryMetricsConfig { Enabled = true },
            },
            _ => null);

        var text = TelemetryHost.Describe(options);

        StringAssert.Contains(text, "metrics");
        StringAssert.Contains(text, "OTLP/http");
        StringAssert.Contains(text, "4318");
        StringAssert.Contains(text, "svc");
        Assert.IsFalse(text.Contains("traces", StringComparison.Ordinal));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
