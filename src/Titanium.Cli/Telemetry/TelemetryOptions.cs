using System.Globalization;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Telemetry;

/// <summary>How a trace is sampled. The mapping to SDK samplers lives in <see cref="TelemetryHost" />.</summary>
internal enum TraceSamplerKind
{
    /// <summary>Every trace is recorded, whatever the caller decided.</summary>
    Always,

    /// <summary><see cref="TelemetryOptions.SamplerRatio" /> of traces, chosen by trace id.</summary>
    Ratio,

    /// <summary>The caller's sampled flag wins; new roots use <see cref="TelemetryOptions.SamplerRatio" />.</summary>
    ParentBased,
}

/// <summary>
///     <c>server.telemetry</c> with every default filled in: a value in the config file wins, then an
///     <c>OTEL_*</c> environment variable, then the built-in default. Resolving is pure, so tests drive it
///     with a dictionary instead of the process environment.
/// </summary>
internal sealed record TelemetryOptions
{
    internal const string DefaultServiceName = "titanium";
    internal const int DefaultTimeoutMs = 10_000;
    internal const int DefaultMetricsIntervalMs = 60_000;
    private const string HttpProtocol = "http";

    public bool TracesEnabled { get; init; }

    public bool MetricsEnabled { get; init; }

    public bool Active => TracesEnabled || MetricsEnabled;

    /// <summary>True for OTLP over gRPC, false for OTLP over HTTP with protobuf.</summary>
    public bool Grpc { get; init; } = true;

    public Uri TracesEndpoint { get; init; } = new("http://localhost:4317");

    public Uri MetricsEndpoint { get; init; } = new("http://localhost:4317");

    /// <summary>Request headers as <c>key=value,key2=value2</c>, or null.</summary>
    public string? Headers { get; init; }

    public int TimeoutMs { get; init; } = DefaultTimeoutMs;

    public TraceSamplerKind Sampler { get; init; } = TraceSamplerKind.ParentBased;

    public double SamplerRatio { get; init; } = 1.0;

    public int MetricsIntervalMs { get; init; } = DefaultMetricsIntervalMs;

    public string ServiceName { get; init; } = DefaultServiceName;

    public IReadOnlyDictionary<string, string> ResourceAttributes { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<string> HeaderAllowList { get; init; } = [];

    /// <summary>Spans buffered for export. A span that arrives when the queue is full is dropped and counted.</summary>
    public int MaxQueueSize { get; init; } = 2048;

    public int MaxExportBatchSize { get; init; } = 512;

    public int ScheduleDelayMs { get; init; } = 5000;

    public int ExportTimeoutMs { get; init; } = 30_000;

    /// <summary>Longest the final flush may take. A collector that is down never holds the process open longer.</summary>
    public int ShutdownTimeoutMs { get; init; } = 5000;

    public static TelemetryOptions Resolve(TelemetryConfig? config, Func<string, string?> env)
    {
        if (IsTrue(env("OTEL_SDK_DISABLED")))
        {
            return new TelemetryOptions();
        }

        var otlp = config?.Otlp;
        var protocolText = Choose(otlp?.Protocol, env("OTEL_EXPORTER_OTLP_PROTOCOL"));
        var grpc = protocolText is null || !string.Equals(NormalizeProtocol(protocolText), HttpProtocol, StringComparison.Ordinal);

        var insecure = otlp?.Insecure ?? IsTrue(env("OTEL_EXPORTER_OTLP_INSECURE"));
        var (sampler, ratio) = ResolveSampler(config?.Traces, env);

        return new TelemetryOptions
        {
            TracesEnabled = config?.Traces?.Enabled ?? ExporterIsOtlp(env("OTEL_TRACES_EXPORTER")),
            MetricsEnabled = config?.Metrics?.Enabled ?? ExporterIsOtlp(env("OTEL_METRICS_EXPORTER")),
            Grpc = grpc,
            TracesEndpoint = ResolveEndpoint(otlp?.Endpoint, env, "TRACES", "v1/traces", grpc, insecure),
            MetricsEndpoint = ResolveEndpoint(otlp?.Endpoint, env, "METRICS", "v1/metrics", grpc, insecure),
            Headers = ResolveHeaders(otlp?.HeadersEnv, env),
            TimeoutMs = otlp?.TimeoutMs ?? ParseInt(env("OTEL_EXPORTER_OTLP_TIMEOUT")) ?? DefaultTimeoutMs,
            Sampler = sampler,
            SamplerRatio = ratio,
            MetricsIntervalMs = config?.Metrics?.IntervalMs
                                ?? ParseInt(env("OTEL_METRIC_EXPORT_INTERVAL")) ?? DefaultMetricsIntervalMs,
            ServiceName = ResolveServiceName(config, env),
            ResourceAttributes = ResolveResourceAttributes(config, env),
            HeaderAllowList = config?.Traces?.HeaderAllowList?.Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim()).ToArray() ?? [],
            MaxQueueSize = Positive(ParseInt(env("OTEL_BSP_MAX_QUEUE_SIZE")), 2048),
            MaxExportBatchSize = Positive(ParseInt(env("OTEL_BSP_MAX_EXPORT_BATCH_SIZE")), 512),
            ScheduleDelayMs = Positive(ParseInt(env("OTEL_BSP_SCHEDULE_DELAY")), 5000),
            ExportTimeoutMs = Positive(ParseInt(env("OTEL_BSP_EXPORT_TIMEOUT")), 30_000),
        };
    }

    private static (TraceSamplerKind Kind, double Ratio) ResolveSampler(
        TelemetryTracesConfig? traces, Func<string, string?> env)
    {
        var fromEnv = env("OTEL_TRACES_SAMPLER")?.Trim().ToLowerInvariant();
        var kind = TraceSamplerKind.ParentBased;
        double? implied = null;
        if (traces?.Sampler is { } configured)
        {
            kind = configured.Trim().ToLowerInvariant() switch
            {
                "always" => TraceSamplerKind.Always,
                "ratio" => TraceSamplerKind.Ratio,
                _ => TraceSamplerKind.ParentBased,
            };
        }
        else
        {
            switch (fromEnv)
            {
                case "always_on":
                    kind = TraceSamplerKind.Always;
                    break;
                case "always_off":
                    kind = TraceSamplerKind.Ratio;
                    implied = 0;
                    break;
                case "traceidratio":
                    kind = TraceSamplerKind.Ratio;
                    break;
                case "parentbased_always_off":
                    implied = 0;
                    break;
                case "parentbased_always_on":
                case "parentbased_traceidratio":
                default:
                    break;
            }
        }

        var ratio = traces?.Ratio ?? implied ?? ParseDouble(env("OTEL_TRACES_SAMPLER_ARG")) ?? 1.0;
        return (kind, Math.Clamp(ratio, 0.0, 1.0));
    }

    private static Uri ResolveEndpoint(
        string? configured, Func<string, string?> env, string signal, string path, bool grpc, bool insecure)
    {
        // A signal-specific variable names the full URL, so nothing is appended to it.
        string raw;
        var appendPath = true;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            raw = configured.Trim();
        }
        else if (env($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT") is { Length: > 0 } specific)
        {
            raw = specific.Trim();
            appendPath = false;
        }
        else if (env("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 } general)
        {
            raw = general.Trim();
        }
        else
        {
            raw = grpc ? "http://localhost:4317" : "http://localhost:4318";
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = (insecure ? "http://" : "https://") + raw;
        }

        var builder = new UriBuilder(raw);
        if (!grpc && appendPath)
        {
            var current = builder.Path.TrimEnd('/');
            const char urlPathSeparator = '/';
            builder.Path = current.EndsWith(urlPathSeparator + path, StringComparison.Ordinal)
                ? current
                : string.Concat(current, urlPathSeparator.ToString(), path);
        }

        return builder.Uri;
    }

    private static string? ResolveHeaders(string? headersEnv, Func<string, string?> env)
    {
        var value = !string.IsNullOrWhiteSpace(headersEnv) ? env(headersEnv.Trim()) : env("OTEL_EXPORTER_OTLP_HEADERS");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ResolveServiceName(TelemetryConfig? config, Func<string, string?> env)
    {
        if (!string.IsNullOrWhiteSpace(config?.ServiceName))
        {
            return config.ServiceName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(env("OTEL_SERVICE_NAME")))
        {
            return env("OTEL_SERVICE_NAME")!.Trim();
        }

        var fromResource = ParseKeyValues(env("OTEL_RESOURCE_ATTRIBUTES"));
        return fromResource.TryGetValue("service.name", out var name) && name.Length > 0 ? name : DefaultServiceName;
    }

    private static Dictionary<string, string> ResolveResourceAttributes(TelemetryConfig? config, Func<string, string?> env)
    {
        var merged = ParseKeyValues(env("OTEL_RESOURCE_ATTRIBUTES"));
        if (config?.ResourceAttributes is { } configured)
        {
            foreach (var pair in configured)
            {
                merged[pair.Key.Trim()] = pair.Value;
            }
        }

        // service.name is set separately from the resolved service name.
        merged.Remove("service.name");
        return merged;
    }

    /// <summary>Parses <c>k=v,k2=v2</c>. Values are percent-decoded, as the OpenTelemetry specification requires.</summary>
    internal static Dictionary<string, string> ParseKeyValues(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            var key = part[..split].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            try
            {
                result[key] = Uri.UnescapeDataString(part[(split + 1)..].Trim());
            }
            catch (UriFormatException)
            {
                result[key] = part[(split + 1)..].Trim();
            }
        }

        return result;
    }

    private static string? Choose(string? configured, string? fromEnv) =>
        !string.IsNullOrWhiteSpace(configured) ? configured : fromEnv;

    private static string NormalizeProtocol(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "http" or "http/protobuf" => HttpProtocol,
            _ => "grpc",
        };

    private static bool ExporterIsOtlp(string? value) =>
        value is not null && value.Split(',', StringSplitOptions.TrimEntries)
            .Contains("otlp", StringComparer.OrdinalIgnoreCase);

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;
}
