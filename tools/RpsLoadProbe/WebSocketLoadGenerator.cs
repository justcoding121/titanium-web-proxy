using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Persistent <see cref="ClientWebSocket"/> workers. Each echo round-trip counts as one RPS sample.
/// </summary>
internal static class WebSocketLoadGenerator
{
    private static readonly byte[] PingPayload = "twp-ws-ping"u8.ToArray();

    public static async Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency, TimeSpan duration,
        bool collectLatency, CancellationToken cancellationToken)
    {
        var targets = EmbeddedLoadGenerator.ResolveTargets(options)
            .Select(ToWebSocketUri).ToArray();
        var ok = 0L;
        var errors = 0L;
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
                ClientWebSocket? socket = null;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var target = targets[rr++ % targets.Length];
                        var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                        try
                        {
                            if (socket == null || socket.State != WebSocketState.Open)
                            {
                                socket?.Dispose();
                                socket = await ConnectAsync(target, cts.Token);
                            }

                            await socket.SendAsync(PingPayload, WebSocketMessageType.Binary, endOfMessage: true,
                                cts.Token);
                            var receive = new byte[PingPayload.Length + 16];
                            var result = await socket.ReceiveAsync(receive, cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "peer-close", cts.Token);
                                socket.Dispose();
                                socket = null;
                                Interlocked.Increment(ref errors);
                                continue;
                            }

                            if (result.Count < 1)
                                throw new InvalidOperationException("empty echo");

                            Interlocked.Increment(ref ok);
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
                                ProbeLog.Error($"  [ws] first error: {firstError}");
                            }

                            Interlocked.Increment(ref errors);
                            if (requestSw != null)
                            {
                                requestSw.Stop();
                                latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                            }

                            try { socket?.Dispose(); } catch { /* ignore */ }
                            socket = null;
                        }
                    }
                }
                finally
                {
                    try { socket?.Dispose(); } catch { /* ignore */ }
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
            Generator: "dotnet-websocket",
            Concurrency: concurrency,
            DurationSeconds: elapsed,
            Ok: ok,
            Errors: errors,
            Rps: ok / elapsed,
            ErrorRatePercent: total == 0 ? 100 : 100.0 * errors / total,
            P50Ms: EmbeddedLoadGenerator.Percentile(samples, 0.50),
            P99Ms: EmbeddedLoadGenerator.Percentile(samples, 0.99),
            MaxMs: samples.Length == 0 ? 0 : samples[^1],
            NegotiatedVersionHint: "websocket");
    }

    private static Uri ToWebSocketUri(Uri target)
    {
        var builder = new UriBuilder(target)
        {
            Scheme = target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/ws",
            Query = string.Empty
        };
        return builder.Uri;
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri target, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(target, connectCts.Token);
        return socket;
    }
}
