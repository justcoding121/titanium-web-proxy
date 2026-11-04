using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Telemetry;

/// <summary>
///     Owns the OpenTelemetry SDK for the process. The proxy libraries only publish
///     <see cref="ActivitySource" /> spans and a <see cref="Meter" />; this attaches an OTLP exporter to them by
///     name, so the NuGet packages carry no OpenTelemetry dependency.
///     <para>
///         Nothing here can stall a request. Spans are handed to a bounded queue that drops when it is full
///         (counted in <c>twp.telemetry.dropped</c>), and export, retry and backoff run on the SDK's own thread.
///         A collector that is down costs a dropped batch, not latency.
///     </para>
/// </summary>
internal sealed class TelemetryHost : IDisposable
{
    internal const string SourceName = "Titanium.Web.Proxy";

    /// <summary>Lua and WASM plugin instruments, including the metrics scripts record.</summary>
    internal const string ScriptingMeterName = "Titanium.Web.Proxy.Scripting";

    private readonly TelemetryOptions options;
    private readonly TracerProvider? tracer;
    private readonly MeterProvider? meter;
    private readonly BatchActivityExportProcessor? processor;
    private readonly ObservedExporter? exporter;
    private int disposed;

    private TelemetryHost(TelemetryOptions options)
    {
        this.options = options;
        var resource = BuildResource(options);

        if (options.TracesEnabled)
        {
            exporter = new ObservedExporter(ExporterOptions(options, options.TracesEndpoint));
            processor = new BatchActivityExportProcessor(
                exporter,
                options.MaxQueueSize,
                options.ScheduleDelayMs,
                options.ExportTimeoutMs,
                options.MaxExportBatchSize);
            tracer = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(SourceName)
                .SetSampler(BuildSampler(options))
                .AddProcessor(processor)
                .Build();
        }

        if (options.MetricsEnabled)
        {
            var reader = new PeriodicExportingMetricReader(
                new OtlpMetricExporter(ExporterOptions(options, options.MetricsEndpoint)),
                options.MetricsIntervalMs,
                options.TimeoutMs);
            meter = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(SourceName, TelemetryInstruments.MeterName, ScriptingMeterName)
                .AddReader(reader)
                .Build();
        }

        TelemetryInstruments.Register(this);
    }

    /// <summary>Spans dropped because the export queue was full.</summary>
    internal long QueueDropped => processor is null ? 0 : DroppedCountOf(processor);

    /// <summary>Spans handed to an exporter that then failed to deliver them.</summary>
    internal long ExportFailed => exporter?.FailedSpans ?? 0;

    internal TelemetryOptions Options => options;

    /// <summary>
    ///     Resolves <c>server.telemetry</c> and, when a signal is on, starts the exporters and turns on the
    ///     proxy's span production. Returns null when telemetry is off or could not start; a bad collector
    ///     setting never stops the proxy.
    /// </summary>
    public static TelemetryHost? TryStart(
        ProxyServer proxy, TelemetryConfig? config, Func<string, string?> env, Action<string> warn) =>
        TryStart(proxy, TelemetryOptions.Resolve(config, env), warn);

    public static TelemetryHost? TryStart(ProxyServer proxy, TelemetryOptions options, Action<string> warn)
    {
        if (!options.Active)
        {
            return null;
        }

        try
        {
            var host = new TelemetryHost(options);
            if (options.TracesEnabled)
            {
                proxy.TracingEnabled = true;
                proxy.PropagateTraceContext = true;
                proxy.TracingHeaderAllowList = options.HeaderAllowList.Count > 0 ? options.HeaderAllowList : null;
            }

            return host;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warn("Telemetry is off: " + ex.Message);
            return null;
        }
    }

    /// <summary>One line for the startup banner. Names the endpoint and signals, never the headers.</summary>
    internal static string Describe(TelemetryOptions options)
    {
        var signals = string.Join("+", new[] { options.TracesEnabled ? "traces" : null, options.MetricsEnabled ? "metrics" : null }.Where(s => s is not null));
        var endpoint = options.TracesEnabled ? options.TracesEndpoint : options.MetricsEndpoint;
        return $"Telemetry: {signals} over OTLP/{(options.Grpc ? "grpc" : "http")} to {endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}{endpoint.AbsolutePath} (service {options.ServiceName})";
    }
    /// <summary>Exports whatever is queued, within the shutdown deadline. Never throws.</summary>
    public bool ForceFlush()
    {
        var ok = true;
        try
        {
            ok &= tracer?.ForceFlush(options.ShutdownTimeoutMs) ?? true;
            ok &= meter?.ForceFlush(options.ShutdownTimeoutMs) ?? true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ok = false;
        }

        return ok;
    }

    /// <summary>Flushes, then stops the exporters. The deadline bounds the wait on a collector that is down.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            tracer?.ForceFlush(options.ShutdownTimeoutMs);
            tracer?.Shutdown(options.ShutdownTimeoutMs);
            meter?.ForceFlush(options.ShutdownTimeoutMs);
            meter?.Shutdown(options.ShutdownTimeoutMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Telemetry is best-effort at exit.
        }
        finally
        {
            TelemetryInstruments.Unregister(this);
            tracer?.Dispose();
            meter?.Dispose();
        }
    }

    /// <summary>
    ///     The SDK keeps the queue's drop counter internal. It is read through a delegate built once; the package
    ///     version is pinned and the queue-overflow test fails loudly if an upgrade moves it.
    /// </summary>
    private static readonly Func<BatchActivityExportProcessor, long> DroppedCountOf = BuildDroppedCountReader();

    private static Func<BatchActivityExportProcessor, long> BuildDroppedCountReader()
    {
        var property = typeof(BatchExportProcessor<Activity>).GetProperty(
            "DroppedCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); // NOSONAR S3011 -- SDK exposes DroppedCount only via non-public API; pinned package version.
        var getter = property?.GetGetMethod(nonPublic: true);
        if (getter is null)
        {
            return static _ => 0;
        }

        var read = (Func<BatchExportProcessor<Activity>, long>)Delegate.CreateDelegate(
            typeof(Func<BatchExportProcessor<Activity>, long>), getter);
        return processor => read(processor);
    }
    private static Sampler BuildSampler(TelemetryOptions options) =>
        options.Sampler switch
        {
            TraceSamplerKind.Always => new AlwaysOnSampler(),
            TraceSamplerKind.Ratio => new TraceIdRatioBasedSampler(options.SamplerRatio),
            _ => new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SamplerRatio)),
        };

    private static ResourceBuilder BuildResource(TelemetryOptions options)
    {
        var version = typeof(TelemetryHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        var attributes = options.ResourceAttributes
            .Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value));
        return ResourceBuilder.CreateEmpty()
            .AddService(options.ServiceName, serviceVersion: version)
            .AddAttributes(attributes);
    }

    private static OtlpExporterOptions ExporterOptions(TelemetryOptions options, Uri endpoint) =>
        new()
        {
            Endpoint = endpoint,
            Protocol = options.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf,
            Headers = options.Headers,
            TimeoutMilliseconds = options.TimeoutMs,
        };

    /// <summary>
    ///     The OTLP exporter, counting spans it failed to deliver. An exception from the OTLP exporter is a
    ///     failed batch, never an unhandled error on the SDK's export thread. It is a subclass (not a wrapper)
    ///     so the SDK hands it the provider resource itself.
    /// </summary>
    private sealed class ObservedExporter : OtlpTraceExporter
    {
        private long failedSpans;

        public ObservedExporter(OtlpExporterOptions options) : base(options)
        {
        }

        public long FailedSpans => Interlocked.Read(ref failedSpans);

        public override ExportResult Export(in Batch<Activity> activityBatch)
        {
            var count = activityBatch.Count;
            try
            {
                var result = base.Export(activityBatch);
                if (result != ExportResult.Success)
                {
                    Interlocked.Add(ref failedSpans, count);
                }

                return result;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Add(ref failedSpans, count);
                return ExportResult.Failure;
            }
        }
    }
}

/// <summary>
///     <c>twp.telemetry.dropped</c>: spans that never reached the collector, by reason. It lives on its own
///     meter so the core catalog of proxy instruments is unchanged, and is exported over OTLP with the rest.
/// </summary>
internal static class TelemetryInstruments
{
    internal const string MeterName = "Titanium.Web.Proxy.Telemetry";
    internal const string DroppedName = "twp.telemetry.dropped";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly object Gate = new();
    private static readonly List<TelemetryHost> Hosts = [];
    private static long retiredQueueDropped;
    private static long retiredExportFailed;

    private static readonly ObservableCounter<long> Dropped = // NOSONAR S1144 -- Strong reference keeps the observable instrument registered.
        Meter.CreateObservableCounter(DroppedName, Observe, "{span}",
            "Spans that never reached the collector, by reason: queue_full when the bounded export queue had no room, export_failed when the collector refused or was unreachable.");

    internal static void Register(TelemetryHost host)
    {
        _ = Dropped;
        lock (Gate)
        {
            Hosts.Add(host);
        }
    }

    internal static void Unregister(TelemetryHost host)
    {
        lock (Gate)
        {
            if (Hosts.Remove(host))
            {
                // A counter must not go backwards when a host is retired, so its totals are kept.
                retiredQueueDropped += host.QueueDropped;
                retiredExportFailed += host.ExportFailed;
            }
        }
    }

    private static IEnumerable<Measurement<long>> Observe()
    {
        long queue;
        long failed;
        lock (Gate)
        {
            queue = retiredQueueDropped;
            failed = retiredExportFailed;
            foreach (var host in Hosts)
            {
                queue += host.QueueDropped;
                failed += host.ExportFailed;
            }
        }

        yield return new Measurement<long>(queue, new KeyValuePair<string, object?>("reason", "queue_full"));
        yield return new Measurement<long>(failed, new KeyValuePair<string, object?>("reason", "export_failed"));
    }
}
