using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using Grpc.Net.Client;
using Titanium.Web.Proxy.RpsLoadProbe.GrpcEcho;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Unary gRPC load generator (RPC/s @ concurrency). Uses Grpc.Net.Client against H2 TLS listen.
/// </summary>
internal static class GrpcLoadGenerator
{
    public const string GeneratorName = "grpc-unary";

    public static Task WarmupAsync(Uri target, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken) =>
        RunAsync(target, concurrency, duration, collectLatency: false, cancellationToken);

    public static Task<LoadResult> RunAsync(Uri target, int concurrency, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        RunAsync(target, concurrency, duration, collectLatency: true, cancellationToken);

    private static async Task<LoadResult> RunAsync(Uri target, int concurrency, TimeSpan duration,
        bool collectLatency, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };
        using var channel = GrpcChannel.ForAddress(target, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true
        });
        var client = new Echo.EchoClient(channel);
        var request = new EchoRequest { Message = "x" };

        var ok = 0L;
        var errors = 0L;
        var latencies = collectLatency ? new ConcurrentBag<long>() : null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);
        var sw = Stopwatch.StartNew();
        var workers = new Task[Math.Max(1, concurrency)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var callSw = collectLatency ? Stopwatch.StartNew() : null;
                    try
                    {
                        _ = await client.UnaryEchoAsync(request, cancellationToken: cts.Token);
                        Interlocked.Increment(ref ok);
                        if (callSw != null)
                            latencies!.Add(callSw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }, CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            // workers swallow; ignore aggregate
        }

        sw.Stop();
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var total = ok + errors;
        var samples = latencies?.ToArray() ?? [];
        Array.Sort(samples);
        return new LoadResult(
            GeneratorName,
            concurrency,
            seconds,
            ok,
            errors,
            ok / seconds,
            total == 0 ? 100 : 100.0 * errors / total,
            Percentile(samples, 0.50),
            Percentile(samples, 0.99),
            samples.Length == 0 ? 0 : samples[^1],
            "h2-grpc");
    }

    private static double Percentile(long[] sortedMs, double p)
    {
        if (sortedMs.Length == 0)
            return 0;
        var idx = (int)Math.Clamp(Math.Ceiling(p * sortedMs.Length) - 1, 0, sortedMs.Length - 1);
        return sortedMs[idx];
    }
}
