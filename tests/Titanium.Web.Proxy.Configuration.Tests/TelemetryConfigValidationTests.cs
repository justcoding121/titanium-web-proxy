using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary><c>server.telemetry</c> is optional; every key that is present must hold a usable value.</summary>
[TestClass]
public class TelemetryConfigValidationTests
{
    private static string Errors(TelemetryConfig? telemetry) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Server = new ServerConfig { Telemetry = telemetry } }));

    [TestMethod]
    public void AbsentAndEmpty_AreAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(null));
        Assert.AreEqual(string.Empty, Errors(new TelemetryConfig()));
    }

    [TestMethod]
    public void FullyPopulated_IsAccepted()
    {
        var config = new TelemetryConfig
        {
            ServiceName = "edge",
            ResourceAttributes = new Dictionary<string, string> { ["deployment.environment"] = "prod" },
            Otlp = new OtlpExporterConfig
            {
                Endpoint = "https://collector.example:4318",
                Protocol = "http",
                HeadersEnv = "OTLP_HEADERS",
                TimeoutMs = 5000,
                Insecure = false,
            },
            Traces = new TelemetryTracesConfig
            {
                Enabled = true,
                Sampler = "ratio",
                Ratio = 0.25,
                HeaderAllowList = ["x-request-id"],
            },
            Metrics = new TelemetryMetricsConfig { Enabled = true, IntervalMs = 15000 },
        };

        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void HostAndPortEndpoint_IsAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(new TelemetryConfig { Otlp = new OtlpExporterConfig { Endpoint = "collector:4317" } }));
    }

    [TestMethod]
    public void BadValues_AreRejectedWithTheirKey()
    {
        StringAssert.Contains(Errors(new TelemetryConfig { ServiceName = " " }), "server.telemetry.serviceName");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Otlp = new OtlpExporterConfig { Protocol = "udp" } }),
            "server.telemetry.otlp.protocol");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Otlp = new OtlpExporterConfig { Endpoint = "ftp://collector" } }),
            "server.telemetry.otlp.endpoint");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Otlp = new OtlpExporterConfig { TimeoutMs = 0 } }),
            "server.telemetry.otlp.timeoutMs");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Otlp = new OtlpExporterConfig { HeadersEnv = "A=B" } }),
            "server.telemetry.otlp.headersEnv");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Traces = new TelemetryTracesConfig { Sampler = "sometimes" } }),
            "server.telemetry.traces.sampler");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Traces = new TelemetryTracesConfig { Ratio = 1.5 } }),
            "server.telemetry.traces.ratio");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Traces = new TelemetryTracesConfig { HeaderAllowList = ["bad name"] } }),
            "server.telemetry.traces.headerAllowList");
        StringAssert.Contains(
            Errors(new TelemetryConfig { Metrics = new TelemetryMetricsConfig { IntervalMs = 10 } }),
            "server.telemetry.metrics.intervalMs");
    }

    [TestMethod]
    public void Yaml_LoadsEveryKey_AndTheModelKnowsThem()
    {
        const string yaml = """
            server:
              telemetry:
                serviceName: edge
                resourceAttributes:
                  deployment.environment: prod
                otlp:
                  endpoint: http://collector:4318
                  protocol: http
                  headersEnv: OTLP_HEADERS
                  timeoutMs: 5000
                  insecure: true
                traces:
                  enabled: true
                  sampler: parentbased
                  ratio: 0.5
                  headerAllowList: [x-request-id]
                metrics:
                  enabled: true
                  intervalMs: 10000
            """;

        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);

        var telemetry = TwpConfigLoader.LoadYaml(yaml).Server!.Telemetry!;
        Assert.AreEqual("edge", telemetry.ServiceName);
        Assert.AreEqual("prod", telemetry.ResourceAttributes!["deployment.environment"]);
        Assert.AreEqual("http", telemetry.Otlp!.Protocol);
        Assert.AreEqual("OTLP_HEADERS", telemetry.Otlp.HeadersEnv);
        Assert.AreEqual(5000, telemetry.Otlp.TimeoutMs);
        Assert.IsTrue(telemetry.Otlp.Insecure);
        Assert.IsTrue(telemetry.Traces!.Enabled);
        Assert.AreEqual(0.5, telemetry.Traces.Ratio);
        CollectionAssert.AreEqual(new[] { "x-request-id" }, telemetry.Traces.HeaderAllowList!.ToArray());
        Assert.AreEqual(10000, telemetry.Metrics!.IntervalMs);
    }

    [TestMethod]
    public void UnknownKey_IsReported()
    {
        var unknown = UnknownConfigKeyScanner.Scan("server:\n  telemetry:\n    traces:\n      samplr: always\n", yaml: true);
        Assert.AreEqual(1, unknown.Count);
    }
}
