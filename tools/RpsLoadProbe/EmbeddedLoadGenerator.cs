using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal sealed record LoadResult(
    string Generator,
    int Concurrency,
    double DurationSeconds,
    long Ok,
    long Errors,
    double Rps,
    double ErrorRatePercent,
    double P50Ms,
    double P99Ms,
    double MaxMs);

/// <summary>
/// Embedded SocketsHttpHandler worker pool. Used when bombardier/wrk is not on PATH.
/// Labeled as "dotnet-httpclient" in CSV — not a wrk equivalent.
/// </summary>
internal static class EmbeddedLoadGenerator
{
    public static async Task WarmupAsync(Uri target, string? explicitProxyUrl, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await RunAsync(target, explicitProxyUrl, concurrency, duration, collectLatency: false, cancellationToken);
    }

    public static async Task<LoadResult> RunAsync(Uri target, string? explicitProxyUrl, int concurrency,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        return await RunAsync(target, explicitProxyUrl, concurrency, duration, collectLatency: true, cancellationToken);
    }

    private static async Task<LoadResult> RunAsync(Uri target, string? explicitProxyUrl, int concurrency,
        TimeSpan duration, bool collectLatency, CancellationToken cancellationToken)
    {
        using var handler = CreateHandler(explicitProxyUrl);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var ok = 0L;
        var errors = 0L;
        var latencies = collectLatency ? new ConcurrentBag<double>() : null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);

        var sw = Stopwatch.StartNew();
        var workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                    try
                    {
                        using var response = await client.GetAsync(target, HttpCompletionOption.ResponseContentRead,
                            cts.Token);
                        await response.Content.CopyToAsync(Stream.Null, cts.Token);
                        if (response.IsSuccessStatusCode)
                            Interlocked.Increment(ref ok);
                        else
                            Interlocked.Increment(ref errors);

                        if (requestSw != null)
                        {
                            requestSw.Stop();
                            latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                        }
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                        if (requestSw != null)
                        {
                            requestSw.Stop();
                            latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                        }
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers);
        sw.Stop();

        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var total = ok + errors;
        var samples = latencies?.ToArray() ?? Array.Empty<double>();
        Array.Sort(samples);

        return new LoadResult(
            Generator: "dotnet-httpclient",
            Concurrency: concurrency,
            DurationSeconds: elapsed,
            Ok: ok,
            Errors: errors,
            Rps: ok / elapsed,
            ErrorRatePercent: total == 0 ? 100 : 100.0 * errors / total,
            P50Ms: Percentile(samples, 0.50),
            P99Ms: Percentile(samples, 0.99),
            MaxMs: samples.Length == 0 ? 0 : samples[^1]);
    }

    private static SocketsHttpHandler CreateHandler(string? explicitProxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            // Bound concurrency to avoid Windows ephemeral-port exhaustion when the proxy
            // closes connections under load. Still high enough for the saturation ramp.
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            // Probe-only: MITM leaves are minted in a child whose root is not shared with this
            // load-gen process, so we accept any cert (never copy this into product code).
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        if (!string.IsNullOrEmpty(explicitProxyUrl))
        {
            handler.Proxy = new WebProxy(explicitProxyUrl);
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}
