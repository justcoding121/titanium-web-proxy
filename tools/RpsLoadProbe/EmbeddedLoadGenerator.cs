using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

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
    public WorkloadOptions Workload { get; init; } = WorkloadOptions.TinyGet;
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

    private static Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency,
        TimeSpan duration, bool collectLatency, CancellationToken cancellationToken)
    {
        if (options.Workload.IsWebSocket)
            return WebSocketLoadGenerator.RunAsync(options, concurrency, duration, collectLatency, cancellationToken);

        var useRawH1 = options.Workload.IsEarlyResponse
                       && options.HttpVersion.Major < 2
                       && string.IsNullOrEmpty(options.ExplicitProxyUrl);
        if (useRawH1)
            return Http1OverlapLoadGenerator.RunAsync(options, concurrency, duration, collectLatency,
                cancellationToken);

        return RunHttpClientAsync(options, concurrency, duration, collectLatency, cancellationToken);
    }

    private static async Task<LoadResult> RunHttpClientAsync(LoadRequestOptions options, int concurrency,
        TimeSpan duration, bool collectLatency, CancellationToken cancellationToken)
    {
        var targets = ResolveTargets(options);
        var workload = options.Workload;
        var requestBody = workload.RequestBytes > 0 ? new byte[workload.RequestBytes] : null;
        if (requestBody != null)
            Array.Fill(requestBody, (byte)'p');

        using var handler = CreateHandler(options.ExplicitProxyUrl, options.HttpVersion, workload.KeepAlive);
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
                        using var request = new HttpRequestMessage(workload.HttpMethod, target)
                        {
                            Version = options.HttpVersion,
                            VersionPolicy = options.VersionPolicy
                        };
                        if (!workload.KeepAlive)
                            request.Headers.ConnectionClose = true;
                        if (requestBody != null)
                        {
                            request.Content = workload.IsEarlyResponse
                                ? new StreamContent(new ChunkedReadStream(requestBody, 8 * 1024))
                                : new ByteArrayContent(requestBody);
                        }

                        // Prefer headers-first so slow-consumer sleep applies true backpressure.
                        // (H3 previously needed ContentRead because the reverse fast path dropped
                        // large bodies — that is fixed via StreamBodyWriter on ForwardOverTcpFastAsync.)
                        var completion = workload.IsEarlyResponse || workload.IsSlowConsumer
                            ? HttpCompletionOption.ResponseHeadersRead
                            : HttpCompletionOption.ResponseContentRead;
                        using var response = await client.SendAsync(request, completion, cts.Token);
                        if (workload.IsSlowConsumer)
                        {
                            var read = await CopyThrottledAsync(response, workload, cts.Token);
                            if (response.Content.Headers.ContentLength is { } expected && read < expected)
                                throw new InvalidOperationException(
                                    $"slow-consumer short read {read}/{expected} ver={response.Version}");
                        }
                        else
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
                            ProbeLog.Error($"  [http] first error: {firstError}");
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

    internal static async Task<long> CopyThrottledAsync(HttpResponseMessage response, WorkloadOptions workload,
        CancellationToken cancellationToken)
    {
        var chunk = Math.Max(1, workload.ClientReadChunkBytes);
        var sleep = Math.Max(0, workload.ClientReadSleepMs);
        var sink = new ThrottledSink(chunk, sleep);
        await response.Content.CopyToAsync(sink, cancellationToken);
        return sink.BytesWritten;
    }

    internal static IReadOnlyList<Uri> ResolveTargets(LoadRequestOptions options)
    {
        if (options.Targets is { Count: > 0 } list)
            return list;
        if (options.Target != null)
            return [options.Target];
        throw new ArgumentException("LoadRequestOptions requires Target or Targets.");
    }

    private static SocketsHttpHandler CreateHandler(string? explicitProxyUrl, Version httpVersion, bool keepAlive)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = keepAlive ? 256 : 1024,
            PooledConnectionLifetime = keepAlive ? TimeSpan.FromMinutes(10) : TimeSpan.Zero,
            PooledConnectionIdleTimeout = keepAlive ? TimeSpan.FromMinutes(2) : TimeSpan.Zero,
            // Multiplex across HTTP/2 connections under load. A single client H2 connection serializes
            // all DATA writes on ClientWriteLock and fans every stream onto the H2→H1 bridge at once;
            // multiple connections match browser-style fan-out and keep error rates down.
            // Set TWP_RPS_SINGLE_HTTP2_CONNECTION=1 to force one client H2 connection (Memory/RSS A/B).
            EnableMultipleHttp2Connections = !IsTruthyEnv("TWP_RPS_SINGLE_HTTP2_CONNECTION"),
            EnableMultipleHttp3Connections = httpVersion.Major >= 3 &&
                                             !IsTruthyEnv("TWP_RPS_SINGLE_HTTP3_CONNECTION"),
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

    private static bool IsTruthyEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

/// <summary>Readable stream that yields the source in fixed chunks so HttpClient can overlap send/receive.</summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly byte[] data;
    private readonly int chunkSize;
    private int position;

    public ChunkedReadStream(byte[] data, int chunkSize)
    {
        this.data = data;
        this.chunkSize = Math.Max(1, chunkSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position >= data.Length)
            return 0;
        var n = Math.Min(Math.Min(count, chunkSize), data.Length - position);
        Buffer.BlockCopy(data, position, buffer, offset, n);
        position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position >= data.Length)
            return ValueTask.FromResult(0);
        var n = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - position);
        data.AsSpan(position, n).CopyTo(buffer.Span);
        position += n;
        return ValueTask.FromResult(n);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Write sink that sleeps after every <c>chunkSize</c> bytes (slow-consumer backpressure).</summary>
internal sealed class ThrottledSink : Stream
{
    private readonly int chunkSize;
    private readonly int sleepMs;
    private int pending;

    public ThrottledSink(int chunkSize, int sleepMs)
    {
        this.chunkSize = Math.Max(1, chunkSize);
        this.sleepMs = Math.Max(0, sleepMs);
    }

    public long BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        pending += buffer.Length;
        while (pending >= chunkSize && sleepMs > 0)
        {
            await Task.Delay(sleepMs, cancellationToken);
            pending -= chunkSize;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// HTTP/1.1 client that writes the first request chunk, then overlaps the remaining body with the response read.
/// SocketsHttpHandler typically finishes the request before reading on HTTP/1.
/// </summary>
internal static class Http1OverlapLoadGenerator
{
    public static async Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency, TimeSpan duration,
        bool collectLatency, CancellationToken cancellationToken)
    {
        var targets = EmbeddedLoadGenerator.ResolveTargets(options);
        var workload = options.Workload;
        var requestBody = new byte[Math.Max(0, workload.RequestBytes)];
        Array.Fill(requestBody, (byte)'p');
        var earlyAfter = Math.Clamp(workload.EarlyResponseAfterBytes, 1, Math.Max(1, requestBody.Length));

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
                Connection? connection = null;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var target = targets[rr++ % targets.Count];
                        var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                        try
                        {
                            if (connection == null || !workload.KeepAlive || !connection.Matches(target))
                            {
                                connection?.Dispose();
                                connection = await Connection.OpenAsync(target, cts.Token);
                            }

                            await connection.RunOverlappedPostAsync(target, requestBody, earlyAfter, cts.Token);
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
                                ProbeLog.Error($"  [h1-overlap] first error: {firstError}");
                            }

                            Interlocked.Increment(ref errors);
                            if (requestSw != null)
                            {
                                requestSw.Stop();
                                latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                            }

                            connection?.Dispose();
                            connection = null;
                        }
                    }
                }
                finally
                {
                    connection?.Dispose();
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
            Generator: "h1-overlap",
            Concurrency: concurrency,
            DurationSeconds: elapsed,
            Ok: ok,
            Errors: errors,
            Rps: ok / elapsed,
            ErrorRatePercent: total == 0 ? 100 : 100.0 * errors / total,
            P50Ms: EmbeddedLoadGenerator.Percentile(samples, 0.50),
            P99Ms: EmbeddedLoadGenerator.Percentile(samples, 0.99),
            MaxMs: samples.Length == 0 ? 0 : samples[^1],
            NegotiatedVersionHint: "1.1");
    }

    private sealed class Connection : IDisposable
    {
        private readonly TcpClient tcp;
        private readonly Stream stream;
        private readonly string hostKey;
        private readonly byte[] readBuffer = new byte[64 * 1024];
        private int readLen;
        private int readPos;

        private Connection(TcpClient tcp, Stream stream, string hostKey)
        {
            this.tcp = tcp;
            this.stream = stream;
            this.hostKey = hostKey;
        }

        public static async Task<Connection> OpenAsync(Uri target, CancellationToken cancellationToken)
        {
            var host = target.Host;
            var port = target.IsDefaultPort
                ? (target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : target.Port;
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken);
            Stream stream = tcp.GetStream();
            if (target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                }, cancellationToken);
                stream = ssl;
            }

            return new Connection(tcp, stream, $"{host}:{port}");
        }

        public bool Matches(Uri target)
        {
            var port = target.IsDefaultPort
                ? (target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : target.Port;
            return hostKey == $"{target.Host}:{port}";
        }

        public async Task RunOverlappedPostAsync(Uri target, byte[] requestBody, int earlyAfter,
            CancellationToken cancellationToken)
        {
            var path = string.IsNullOrEmpty(target.PathAndQuery) ? "/" : target.PathAndQuery;
            var header = Encoding.ASCII.GetBytes(
                $"POST {path} HTTP/1.1\r\nHost: {target.Authority}\r\nContent-Type: application/octet-stream\r\n" +
                $"Content-Length: {requestBody.Length}\r\nConnection: keep-alive\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            var first = Math.Min(earlyAfter, requestBody.Length);
            if (first > 0)
                await stream.WriteAsync(requestBody.AsMemory(0, first), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var writeRest = Task.CompletedTask;
            if (first < requestBody.Length)
            {
                writeRest = stream.WriteAsync(requestBody.AsMemory(first), cancellationToken).AsTask();
            }

            var status = await ReadStatusAndDrainAsync(cancellationToken);
            await writeRest;
            if (status is < 200 or >= 300)
                throw new InvalidOperationException("non-2xx status=" + status);
        }

        private async Task<int> ReadStatusAndDrainAsync(CancellationToken cancellationToken)
        {
            var header = await ReadUntilHeadersAsync(cancellationToken);
            var status = 0;
            var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
            var firstLine = firstLineEnd < 0 ? header : header[..firstLineEnd];
            var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                int.TryParse(parts[1], out status);

            var contentLength = 0;
            const string cl = "Content-Length:";
            var idx = header.IndexOf(cl, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var lineEnd = header.IndexOf("\r\n", idx, StringComparison.Ordinal);
                var value = (lineEnd < 0 ? header[idx..] : header[idx..lineEnd])[cl.Length..].Trim();
                int.TryParse(value, out contentLength);
            }

            var remaining = contentLength;
            while (remaining > 0)
            {
                if (readPos >= readLen)
                {
                    readLen = await stream.ReadAsync(readBuffer, cancellationToken);
                    readPos = 0;
                    if (readLen == 0)
                        break;
                }

                var take = Math.Min(remaining, readLen - readPos);
                readPos += take;
                remaining -= take;
            }

            return status;
        }

        private async Task<string> ReadUntilHeadersAsync(CancellationToken cancellationToken)
        {
            var acc = new MemoryStream();
            while (true)
            {
                if (readPos >= readLen)
                {
                    readLen = await stream.ReadAsync(readBuffer, cancellationToken);
                    readPos = 0;
                    if (readLen == 0)
                        throw new EndOfStreamException("EOF before HTTP headers.");
                }

                acc.Write(readBuffer, readPos, readLen - readPos);
                var consumed = readLen - readPos;
                readPos = readLen;
                var bytes = acc.ToArray();
                var sep = IndexOfHeaderSeparator(bytes);
                if (sep < 0)
                    continue;

                var leftover = bytes.Length - (sep + 4);
                if (leftover > 0)
                {
                    Buffer.BlockCopy(bytes, sep + 4, readBuffer, 0, leftover);
                    readLen = leftover;
                    readPos = 0;
                }
                else
                {
                    readLen = 0;
                    readPos = 0;
                }

                return Encoding.ASCII.GetString(bytes, 0, sep);
            }
        }

        private static int IndexOfHeaderSeparator(byte[] bytes)
        {
            for (var i = 0; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n'
                    && bytes[i + 2] == (byte)'\r' && bytes[i + 3] == (byte)'\n')
                    return i;
            }

            return -1;
        }

        public void Dispose()
        {
            try { stream.Dispose(); } catch { /* ignore */ }
            try { tcp.Dispose(); } catch { /* ignore */ }
        }
    }
}
