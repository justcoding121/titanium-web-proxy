using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Telemetry;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests.Telemetry;

/// <summary>A config value wins over an OTEL_* variable, which wins over the built-in default.</summary>
[TestClass]
public class TelemetryOptionsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.GetValueOrDefault(key);
    }

    [TestMethod]
    public void Nothing_MeansOffWithDefaults()
    {
        var options = TelemetryOptions.Resolve(null, Env());

        Assert.IsFalse(options.Active);
        Assert.IsTrue(options.Grpc);
        Assert.AreEqual(new Uri("http://localhost:4317"), options.TracesEndpoint);
        Assert.AreEqual(TraceSamplerKind.ParentBased, options.Sampler);
        Assert.AreEqual(1.0, options.SamplerRatio);
        Assert.AreEqual("titanium", options.ServiceName);
        Assert.AreEqual(10_000, options.TimeoutMs);
        Assert.AreEqual(60_000, options.MetricsIntervalMs);
        Assert.AreEqual(2048, options.MaxQueueSize);
    }

    [TestMethod]
    public void OtelExporterVariables_TurnSignalsOn()
    {
        var options = TelemetryOptions.Resolve(null, Env(
            ("OTEL_TRACES_EXPORTER", "otlp"),
            ("OTEL_METRICS_EXPORTER", "console,otlp")));

        Assert.IsTrue(options.TracesEnabled);
        Assert.IsTrue(options.MetricsEnabled);

        var none = TelemetryOptions.Resolve(null, Env(("OTEL_TRACES_EXPORTER", "none")));
        Assert.IsFalse(none.Active);
    }

    [TestMethod]
    public void Config_WinsOverEnvironment()
    {
        var config = new TelemetryConfig
        {
            Traces = new TelemetryTracesConfig { Enabled = false, Sampler = "always" },
            ServiceName = "from-config",
            Otlp = new OtlpExporterConfig { Protocol = "grpc", Endpoint = "http://config:4317", TimeoutMs = 1234 },
        };

        var options = TelemetryOptions.Resolve(config, Env(
            ("OTEL_TRACES_EXPORTER", "otlp"),
            ("OTEL_TRACES_SAMPLER", "always_off"),
            ("OTEL_SERVICE_NAME", "from-env"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://env:4318"),
            ("OTEL_EXPORTER_OTLP_TIMEOUT", "99")));

        Assert.IsFalse(options.TracesEnabled);
        Assert.AreEqual(TraceSamplerKind.Always, options.Sampler);
        Assert.AreEqual("from-config", options.ServiceName);
        Assert.IsTrue(options.Grpc);
        Assert.AreEqual(new Uri("http://config:4317"), options.TracesEndpoint);
        Assert.AreEqual(1234, options.TimeoutMs);
    }

    [TestMethod]
    public void HttpProtocol_AppendsSignalPaths_ExceptForSignalSpecificUrls()
    {
        var general = TelemetryOptions.Resolve(null, Env(
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318")));
        Assert.IsFalse(general.Grpc);
        Assert.AreEqual(new Uri("http://collector:4318/v1/traces"), general.TracesEndpoint);
        Assert.AreEqual(new Uri("http://collector:4318/v1/metrics"), general.MetricsEndpoint);

        var specific = TelemetryOptions.Resolve(null, Env(
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://collector:4318/custom/traces")));
        Assert.AreEqual(new Uri("http://collector:4318/custom/traces"), specific.TracesEndpoint);
        Assert.AreEqual(new Uri("http://localhost:4318/v1/metrics"), specific.MetricsEndpoint);

        var configured = TelemetryOptions.Resolve(
            new TelemetryConfig { Otlp = new OtlpExporterConfig { Protocol = "http", Endpoint = "http://c:4318/v1/traces/" } },
            Env());
        Assert.AreEqual(new Uri("http://c:4318/v1/traces"), configured.TracesEndpoint);
    }

    [TestMethod]
    public void GrpcProtocol_LeavesTheEndpointPathAlone()
    {
        var options = TelemetryOptions.Resolve(null, Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")));
        Assert.AreEqual(new Uri("http://collector:4317"), options.TracesEndpoint);
    }

    [TestMethod]
    public void SchemelessEndpoint_FollowsInsecure()
    {
        var secure = TelemetryOptions.Resolve(
            new TelemetryConfig { Otlp = new OtlpExporterConfig { Endpoint = "collector:4317" } }, Env());
        Assert.AreEqual("https", secure.TracesEndpoint.Scheme);

        var insecure = TelemetryOptions.Resolve(
            new TelemetryConfig { Otlp = new OtlpExporterConfig { Endpoint = "collector:4317", Insecure = true } }, Env());
        Assert.AreEqual("http", insecure.TracesEndpoint.Scheme);
    }

    [TestMethod]
    public void SamplerVariables_MapToTheTree()
    {
        var always = TelemetryOptions.Resolve(null, Env(("OTEL_TRACES_SAMPLER", "always_on")));
        Assert.AreEqual(TraceSamplerKind.Always, always.Sampler);

        var off = TelemetryOptions.Resolve(null, Env(("OTEL_TRACES_SAMPLER", "always_off")));
        Assert.AreEqual(TraceSamplerKind.Ratio, off.Sampler);
        Assert.AreEqual(0.0, off.SamplerRatio);

        var ratio = TelemetryOptions.Resolve(null, Env(
            ("OTEL_TRACES_SAMPLER", "traceidratio"), ("OTEL_TRACES_SAMPLER_ARG", "0.25")));
        Assert.AreEqual(TraceSamplerKind.Ratio, ratio.Sampler);
        Assert.AreEqual(0.25, ratio.SamplerRatio);

        var parent = TelemetryOptions.Resolve(null, Env(
            ("OTEL_TRACES_SAMPLER", "parentbased_traceidratio"), ("OTEL_TRACES_SAMPLER_ARG", "0.5")));
        Assert.AreEqual(TraceSamplerKind.ParentBased, parent.Sampler);
        Assert.AreEqual(0.5, parent.SamplerRatio);

        var clamped = TelemetryOptions.Resolve(null, Env(
            ("OTEL_TRACES_SAMPLER", "traceidratio"), ("OTEL_TRACES_SAMPLER_ARG", "7")));
        Assert.AreEqual(1.0, clamped.SamplerRatio);
    }

    [TestMethod]
    public void SdkDisabled_BeatsEverything()
    {
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig { Traces = new TelemetryTracesConfig { Enabled = true } },
            Env(("OTEL_SDK_DISABLED", "true")));

        Assert.IsFalse(options.Active);
    }

    [TestMethod]
    public void HeadersEnv_NamesTheVariableThatHoldsTheSecret()
    {
        var named = TelemetryOptions.Resolve(
            new TelemetryConfig { Otlp = new OtlpExporterConfig { HeadersEnv = "MY_OTLP_HEADERS" } },
            Env(("MY_OTLP_HEADERS", "authorization=Bearer abc"), ("OTEL_EXPORTER_OTLP_HEADERS", "ignored=1")));
        Assert.AreEqual("authorization=Bearer abc", named.Headers);

        var standard = TelemetryOptions.Resolve(null, Env(("OTEL_EXPORTER_OTLP_HEADERS", "k=v")));
        Assert.AreEqual("k=v", standard.Headers);

        Assert.IsNull(TelemetryOptions.Resolve(null, Env()).Headers);
    }

    [TestMethod]
    public void ResourceAttributes_MergeEnvironmentUnderConfig()
    {
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig
            {
                ResourceAttributes = new Dictionary<string, string> { ["team"] = "edge", ["tier"] = "gateway" },
            },
            Env(("OTEL_RESOURCE_ATTRIBUTES", "team=env,region=eu%2Dwest,service.name=named-by-env")));

        Assert.AreEqual("edge", options.ResourceAttributes["team"]);
        Assert.AreEqual("gateway", options.ResourceAttributes["tier"]);
        Assert.AreEqual("eu-west", options.ResourceAttributes["region"]);
        Assert.AreEqual("named-by-env", options.ServiceName);
        Assert.IsFalse(options.ResourceAttributes.ContainsKey("service.name"));
    }

    [TestMethod]
    public void BatchVariables_SizeTheQueue()
    {
        var options = TelemetryOptions.Resolve(null, Env(
            ("OTEL_BSP_MAX_QUEUE_SIZE", "10"),
            ("OTEL_BSP_MAX_EXPORT_BATCH_SIZE", "5"),
            ("OTEL_BSP_SCHEDULE_DELAY", "100"),
            ("OTEL_BSP_EXPORT_TIMEOUT", "2000")));

        Assert.AreEqual(10, options.MaxQueueSize);
        Assert.AreEqual(5, options.MaxExportBatchSize);
        Assert.AreEqual(100, options.ScheduleDelayMs);
        Assert.AreEqual(2000, options.ExportTimeoutMs);

        var bad = TelemetryOptions.Resolve(null, Env(("OTEL_BSP_MAX_QUEUE_SIZE", "-3")));
        Assert.AreEqual(2048, bad.MaxQueueSize);
    }

    [TestMethod]
    public void Describe_NeverShowsHeaderValues()
    {
        var options = TelemetryOptions.Resolve(
            new TelemetryConfig { Traces = new TelemetryTracesConfig { Enabled = true } },
            Env(("OTEL_EXPORTER_OTLP_HEADERS", "authorization=Bearer very-secret")));

        var text = TelemetryHost.Describe(options);

        Assert.IsFalse(text.Contains("very-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Bearer", StringComparison.Ordinal));
    }
}
