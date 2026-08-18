#pragma warning disable CA1416
#pragma warning disable TWP001

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Minimal HTTP/3 origin (QUIC) for reverse-h3 saturation. Uses the same QuicListener
/// pattern as integration tests.
/// </summary>
internal sealed class QuicHttp3OriginHost : IAsyncDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly QuicListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly byte[] responseBody;

    public QuicHttp3OriginHost(int responseBytes = 0)
    {
        responseBody = OriginServer.BuildResponseBody(
            responseBytes > 0 ? responseBytes : WorkloadOptions.TinyJsonBytes);
        certificate = LoopbackCertificateAuthority.ServerCertificate;
        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0),
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
                DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
                IdleTimeout = TimeSpan.FromSeconds(60),
                MaxInboundBidirectionalStreams = 256,
                MaxInboundUnidirectionalStreams = 3,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http3]
                }
            })
        };

        listener = QuicListener.ListenAsync(options).AsTask().GetAwaiter().GetResult();
        _ = AcceptLoopAsync();
    }

    public int Port => listener.LocalEndPoint.Port;

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(connection));
        }
    }

    private async Task HandleConnectionAsync(QuicConnection connection)
    {
        await using (connection)
        {
            try
            {
                await using var control = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, cts.Token);
                await control.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, cts.Token);
                var settings = new Http3Settings();
                settings.SetQpackMaxTableCapacity(0);
                settings.SetQpackBlockedStreams(0);
                await Http3Frame.WriteAsync(control, Http3FrameType.Settings, settings.Serialize(), cts.Token);

                while (!cts.IsCancellationRequested)
                {
                    var stream = await connection.AcceptInboundStreamAsync(cts.Token);
                    if (stream.Type == QuicStreamType.Unidirectional)
                    {
                        _ = Task.Run(async () =>
                        {
                            await using (stream)
                            {
                                var buf = new byte[4096];
                                while (await stream.ReadAsync(buf, cts.Token) > 0) { }
                            }
                        });
                        continue;
                    }

                    _ = Task.Run(() => HandleRequestStreamAsync(stream));
                }
            }
            catch (OperationCanceledException) { }
            catch (QuicException) { }
        }
    }

    private async Task HandleRequestStreamAsync(QuicStream stream)
    {
        await using (stream)
        {
            try
            {
                var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 64 * 1024, cts.Token);
                if (headersFrame is null || headersFrame.Type != Http3FrameType.Headers)
                    return;

                while (true)
                {
                    var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, cts.Token);
                    if (frame is null) break;
                    if (frame.Type == Http3FrameType.Headers)
                        break;
                }

                var responseHeaders = QpackEncoder.Encode(
                [
                    (":status", "200"),
                    ("content-type", "application/octet-stream"),
                    ("content-length", responseBody.Length.ToString())
                ]);
                await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, responseHeaders, cts.Token);
                await Http3Frame.WriteAsync(stream, Http3FrameType.Data, responseBody, cts.Token);
                stream.CompleteWrites();
            }
            catch (OperationCanceledException) { }
            catch (QuicException) { }
            catch (Http3ConnectionException) { }
            catch (Http3StreamException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        await listener.DisposeAsync();
        cts.Dispose();
        certificate.Dispose();
    }
}

/// <summary>
/// HTTP/3 load generator over <see cref="QuicConnection"/>. Used for UDP-only
/// <c>TransparentQuicProxyEndPoint</c> arms (e.g. MITM H3) where HttpClient has no TCP/Alt-Svc
/// discovery path. Dual-listen reverse H3 uses <c>dotnet-httpclient</c> instead.
/// </summary>
internal static class QuicHttp3LoadGenerator
{
    public static async Task WarmupAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, CancellationToken cancellationToken,
        WorkloadOptions? workload = null)
    {
        await RunAsync(proxyEndPoint, sniHost, authority, concurrency, duration, collectLatency: false,
            cancellationToken, workload);
    }

    public static Task<LoadResult> RunAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, CancellationToken cancellationToken = default,
        WorkloadOptions? workload = null) =>
        RunAsync(proxyEndPoint, sniHost, authority, concurrency, duration, collectLatency: true, cancellationToken,
            workload);

    private static async Task<LoadResult> RunAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, bool collectLatency, CancellationToken cancellationToken,
        WorkloadOptions? workload = null)
    {
        workload ??= WorkloadOptions.TinyGet;
        var requestBody = workload.RequestBytes > 0 ? new byte[workload.RequestBytes] : null;
        if (requestBody != null)
            Array.Fill(requestBody, (byte)'p');
        var method = workload.Method.ToUpperInvariant();
        var keepAlive = workload.KeepAlive;
        var ok = 0L;
        var errors = 0L;
        var latencies = collectLatency ? new ConcurrentBag<double>() : null;
        string? firstError = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);

        // New-connection mode: one QUIC connection per request. Keep-alive: multiplex across a few.
        var connectionCount = keepAlive ? Math.Clamp(concurrency / 8, 1, 8) : concurrency;
        var connections = new QuicConnection?[connectionCount];
        var connectionLocks = new object[connectionCount];
        // Critical H3 unidirectional streams must stay open for the connection lifetime
        // (closing them is H3_CLOSED_CRITICAL_STREAM = 0x104 / 260 — Kestrel aborts).
        var retainedUnidirectional = new ConcurrentBag<QuicStream>();
        for (var i = 0; i < connectionCount; i++)
            connectionLocks[i] = new object();

        try
        {
            if (keepAlive)
            {
                for (var c = 0; c < connectionCount; c++)
                {
                    connections[c] = await ConnectAsync(proxyEndPoint, sniHost, cts.Token);
                    await OpenControlAsync(connections[c]!, retainedUnidirectional, cts.Token);
                    DrainInbound(connections[c]!, cts.Token);
                }
            }

            var sw = Stopwatch.StartNew();
            var workers = new Task[concurrency];
            for (var i = 0; i < concurrency; i++)
            {
                var workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    var slot = workerId % connectionCount;
                    while (!cts.IsCancellationRequested)
                    {
                        QuicConnection? connection = null;
                        var owned = false;
                        if (keepAlive)
                        {
                            lock (connectionLocks[slot])
                                connection = connections[slot];
                        }

                        if (connection == null)
                        {
                            try
                            {
                                connection = await ConnectAsync(proxyEndPoint, sniHost, cts.Token);
                                await OpenControlAsync(connection, retainedUnidirectional, cts.Token);
                                DrainInbound(connection, cts.Token);
                                if (keepAlive)
                                {
                                    lock (connectionLocks[slot])
                                        connections[slot] = connection;
                                }
                                else
                                {
                                    owned = true;
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
                                    firstError = ex.GetType().Name + ": " + ex.Message;
                                    ProbeLog.Error($"  [quic-h3] {firstError}");
                                }

                                Interlocked.Increment(ref errors);
                                break;
                            }
                        }

                        var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                        try
                        {
                            var status = await SendRequestAsync(connection, authority, "/", method, requestBody,
                                cts.Token);
                            if (status is >= 200 and < 300)
                                Interlocked.Increment(ref ok);
                            else
                            {
                                Interlocked.Increment(ref errors);
                                if (firstError == null)
                                {
                                    firstError = "non-2xx status=" + status;
                                    ProbeLog.Error($"  [quic-h3] {firstError}");
                                }
                            }

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
                            var msg = ex.GetType().Name + ": " + ex.Message;
                            if (firstError == null)
                            {
                                firstError = msg;
                                ProbeLog.Error($"  [quic-h3] {firstError}");
                            }

                            Interlocked.Increment(ref errors);
                            if (requestSw != null)
                            {
                                requestSw.Stop();
                                latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                            }

                            if (keepAlive)
                            {
                                QuicConnection? dead;
                                lock (connectionLocks[slot])
                                {
                                    dead = connections[slot];
                                    connections[slot] = null;
                                }

                                if (dead != null)
                                {
                                    try
                                    {
                                        await dead.DisposeAsync();
                                    }
                                    catch
                                    {
                                        // ignore
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (owned && connection != null)
                            {
                                try
                                {
                                    await connection.DisposeAsync();
                                }
                                catch
                                {
                                    // ignore
                                }
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
                Generator: "quic-http3",
                Concurrency: concurrency,
                DurationSeconds: elapsed,
                Ok: ok,
                Errors: errors,
                Rps: ok / elapsed,
                ErrorRatePercent: total == 0 ? 100 : 100.0 * errors / total,
                P50Ms: Percentile(samples, 0.50),
                P99Ms: Percentile(samples, 0.99),
                MaxMs: samples.Length == 0 ? 0 : samples[^1],
                NegotiatedVersionHint: "3.0");
        }
        finally
        {
            while (retainedUnidirectional.TryTake(out var uni))
            {
                try
                {
                    await uni.DisposeAsync();
                }
                catch
                {
                    // ignore
                }
            }

            for (var c = 0; c < connectionCount; c++)
            {
                if (connections[c] != null)
                {
                    try
                    {
                        await connections[c]!.DisposeAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    private static async Task<QuicConnection> ConnectAsync(IPEndPoint endpoint, string sniHost,
        CancellationToken cancellationToken)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = endpoint,
            DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 3,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                TargetHost = sniHost,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };
        // Lossy UDP / stalled networks can hang MsQuic connect; bound it so the ramp progresses.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
        return await QuicConnection.ConnectAsync(options, connectCts.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
    }

    private static async Task OpenControlAsync(QuicConnection connection,
        ConcurrentBag<QuicStream> retainedUnidirectional, CancellationToken cancellationToken)
    {
        var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        retainedUnidirectional.Add(control);
        await control.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, cancellationToken);
        var settings = new Http3Settings();
        settings.SetQpackMaxTableCapacity(0);
        settings.SetQpackBlockedStreams(0);
        await Http3Frame.WriteAsync(control, Http3FrameType.Settings, settings.Serialize(), cancellationToken);

        // RFC 9114: QPACK encoder/decoder streams are critical; open them even with table capacity 0.
        var qpackEncoder = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        retainedUnidirectional.Add(qpackEncoder);
        await qpackEncoder.WriteAsync(new byte[] { (byte)Http3StreamType.QpackEncoder }, cancellationToken);

        var qpackDecoder = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        retainedUnidirectional.Add(qpackDecoder);
        await qpackDecoder.WriteAsync(new byte[] { (byte)Http3StreamType.QpackDecoder }, cancellationToken);
    }

    private static void DrainInbound(QuicConnection connection, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var stream = await connection.AcceptInboundStreamAsync(cancellationToken);
                    _ = Task.Run(async () =>
                    {
                        await using (stream)
                        {
                            var buf = new byte[4096];
                            while (await stream.ReadAsync(buf, cancellationToken) > 0) { }
                        }
                    });
                }
            }
            catch
            {
                // connection closed
            }
        });
    }

    private static async Task<int> SendRequestAsync(QuicConnection connection, string authority, string path,
        string method, byte[]? requestBody, CancellationToken cancellationToken)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, cancellationToken);

        var headerList = new List<(string, string)>
        {
            (":method", method),
            (":scheme", "https"),
            (":authority", authority),
            (":path", path)
        };
        if (requestBody != null)
            headerList.Add(("content-length", requestBody.Length.ToString()));

        var headers = QpackEncoder.Encode(headerList);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, headers, cancellationToken);
        if (requestBody != null)
            await Http3Frame.WriteAsync(stream, Http3FrameType.Data, requestBody, cancellationToken);
        stream.CompleteWrites();

        var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 64 * 1024, cancellationToken);
        if (headersFrame is null || headersFrame.Type != Http3FrameType.Headers)
            throw new InvalidOperationException("Expected response HEADERS frame.");

        var decoded = QpackDecoder.Decode(headersFrame.Payload.Span);
        var status = 0;
        foreach (var (name, value) in decoded)
        {
            if (name == ":status" && int.TryParse(value, out var code))
                status = code;
        }

        while (true)
        {
            // Drain DATA (large bodies) — 1 MiB cap per frame is enough for our probe sizes.
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 1024 * 1024, cancellationToken);
            if (frame is null) break;
            if (frame.Type == Http3FrameType.Headers)
                break;
        }

        return status;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
