using System.Collections.Concurrent;
using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
///     Diagnostic-only per-stage latency decomposition for TWP arms (TWP_RPS_STAGE_TIMING=1).
///     Uses <see cref="Diagnostics.HttpRequestTiming" /> marks captured by the proxy and prints
///     p50/p90/p99 per stage to stderr every 20 seconds. AfterResponse subscription disables the
///     no-interception fast paths, so this must stay opt-in and out of publishable runs.
/// </summary>
internal static class StageTimingCollector
{
    private const int MaxSamples = 500_000;

    // Stage durations in microseconds; -1 = stage not reached / not marked for that session.
    private sealed record Sample(long ClientRead, long ConnWait, long Send, long Ttfb, long Delivery, long Total);

    private static readonly ConcurrentQueue<Sample> Samples = new();
    private static int sampleCount;
    private static Timer? reportTimer;

    // "1" reports to stderr; any other value is treated as an output file path (the parent probe
    // process redirects but does not drain child stderr, so long runs must use a file).
    private static readonly string? OutputFile =
        Environment.GetEnvironmentVariable("TWP_RPS_STAGE_TIMING") is { Length: > 1 } path ? path : null;

    public static void Attach(ProxyServer proxy)
    {
        proxy.AfterResponse += (_, e) =>
        {
            var t = e.Timing;
            if (t != null && sampleCount < MaxSamples)
            {
                var now = DateTime.UtcNow;
                Samples.Enqueue(new Sample(
                    ToMicros(t.ClientRequestReadDuration),
                    ToMicros(t.ConnectionWaitDuration),
                    ToMicros(t.RequestSendDuration),
                    ToMicros(t.TimeToFirstByte),
                    // MarkComplete runs after AfterResponse, so approximate delivery with "now".
                    ToMicros(t.ResponseHeadersReceivedAt.HasValue ? now - t.ResponseHeadersReceivedAt.Value : null),
                    ToMicros(now - t.SessionCreatedAt)));
                Interlocked.Increment(ref sampleCount);
            }

            return Task.CompletedTask;
        };

        reportTimer ??= new Timer(_ => Report(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private static long ToMicros(TimeSpan? value) => value.HasValue ? (long)value.Value.TotalMicroseconds : -1;

    private static void Report()
    {
        var snapshot = Samples.ToArray();
        if (snapshot.Length == 0) return;

        var line =
            $"[stage-timing] n={snapshot.Length} (µs)  " +
            $"clientRead={Percentiles(snapshot, s => s.ClientRead)}  " +
            $"connWait={Percentiles(snapshot, s => s.ConnWait)}  " +
            $"send={Percentiles(snapshot, s => s.Send)}  " +
            $"ttfb={Percentiles(snapshot, s => s.Ttfb)}  " +
            $"delivery={Percentiles(snapshot, s => s.Delivery)}  " +
            $"total={Percentiles(snapshot, s => s.Total)}";

        if (OutputFile != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await File.AppendAllTextAsync(OutputFile, line + Environment.NewLine).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Diagnostic best-effort; never disturb the run.
                }
            });
        }
        else
        {
            ProbeLog.Error(line);
        }
    }

    private static string Percentiles(Sample[] snapshot, Func<Sample, long> selector)
    {
        var values = snapshot.Select(selector).Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (values.Length == 0) return "n/a";
        return string.Create(CultureInfo.InvariantCulture,
            $"{At(values, 0.50)}/{At(values, 0.90)}/{At(values, 0.99)}");
    }

    private static long At(long[] sorted, double p) => sorted[(int)Math.Min(sorted.Length - 1, p * sorted.Length)];
}
