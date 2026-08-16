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
    double MaxMs,
    string NegotiatedVersionHint);

internal sealed class LoadRequestOptions
{
    public Uri? Target { get; init; }
    public IReadOnlyList<Uri>? Targets { get; init; }
    public string? ExplicitProxyUrl { get; init; }
    public Version HttpVersion { get; init; } = System.Net.HttpVersion.Version11;
    public HttpVersionPolicy VersionPolicy { get; init; } = HttpVersionPolicy.RequestVersionOrLower;
}

/// <summary>
/// Embedded SocketsHttpHandler worker pool. Used when bombardier/wrk is not on PATH.
/// Labeled as "dotnet-httpclient" in CSV — not a wrk equivalent.
/// </summary>
internal static class EmbeddedLoadGenerator
{
    public static Task WarmupAsync(LoadRequestOptions options, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken) =>
        RunAsync(options, concurrency, duration, collectLatency: false, cancellationToken);

    public static Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        RunAsync(options, concurrency, duration, collectLatency: true, cancellationToken);

    public static Task WarmupAsync(Uri target, string? explicitProxyUrl, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken) =>
        WarmupAsync(new LoadRequestOptions { Target = target, ExplicitProxyUrl = explicitProxyUrl }, concurrency,
            duration, cancellationToken);

    public static Task<LoadResult> RunAsync(Uri target, string? explicitProxyUrl, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        RunAsync(new LoadRequestOptions { Target = target, ExplicitProxyUrl = explicitProxyUrl }, concurrency,
            duration, cancellationToken);

    private static async Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency,
        TimeSpan duration, bool collectLatency, CancellationToken cancellationToken)
    {
        var targets = ResolveTargets(options);
        using var handler = CreateHandler(options.ExplicitProxyUrl, options.HttpVersion);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = options.HttpVersion,
            DefaultVersionPolicy = options.VersionPolicy
        };

        var ok = 0L;
        var errors = 0L;
        var versionHits = new ConcurrentDictionary<string, long>();
        var latencies = collectLatency ? new ConcurrentBag<double>() : null;
        string? firstError = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);

        var sw = Stopwatch.StartNew();
        var workers = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            var workerId = i;
            workers[i] = Task.Run(async () =>
            {
                var rr = workerId;
                while (!cts.IsCancellationRequested)
                {
                    var target = targets[rr++ % targets.Count];
                    var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, target)
                        {
                            Version = options.HttpVersion,
                            VersionPolicy = options.VersionPolicy
                        };
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                            cts.Token);
                        await response.Content.CopyToAsync(Stream.Null, cts.Token);
                        versionHits.AddOrUpdate(response.Version.ToString(), 1, static (_, n) => n + 1);
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
                    catch (Exception ex)
                    {
                        if (firstError == null)
                        {
                            var detail = ex.ToString();
                            if (detail.Length > 500) detail = detail[..500];
                            firstError = detail;
                            // #region agent log
                            DebugSessionLog.Write("C", "EmbeddedLoadGenerator", "first-error",
                                new { error = firstError, target = target.ToString(), version = options.HttpVersion.ToString() });
                            // #endregion
                        }

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
        var versionHint = string.Join(',', versionHits.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));

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
            MaxMs: samples.Length == 0 ? 0 : samples[^1],
            NegotiatedVersionHint: versionHint);
    }

    private static IReadOnlyList<Uri> ResolveTargets(LoadRequestOptions options)
    {
        if (options.Targets is { Count: > 0 } list)
            return list;
        if (options.Target != null)
            return [options.Target];
        throw new ArgumentException("LoadRequestOptions requires Target or Targets.");
    }

    private static SocketsHttpHandler CreateHandler(string? explicitProxyUrl, Version httpVersion)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // Multiplex across HTTP/2 connections under load. A single client H2 connection serializes
            // all DATA writes on ClientWriteLock and fans every stream onto the H2→H1 bridge at once;
            // multiple connections match browser/nginx-style fan-out and keep error rates down.
            EnableMultipleHttp2Connections = true,
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
