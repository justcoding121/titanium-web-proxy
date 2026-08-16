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
/// Minimal HTTP/3 origin (QUIC) for reverse-h3 saturation. HttpClient cannot drive a
/// UDP-only <c>TransparentQuicProxyEndPoint</c>, so the probe uses the same QuicListener
/// pattern as integration tests.
/// </summary>
internal sealed class QuicHttp3OriginHost : IAsyncDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly QuicListener listener;
    private readonly CancellationTokenSource cts = new();

    public QuicHttp3OriginHost()
    {
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

                var body = Encoding.UTF8.GetBytes(OriginServer.ResponseBody);
                var responseHeaders = QpackEncoder.Encode(
                [
                    (":status", "200"),
                    ("content-type", "application/json"),
                    ("content-length", body.Length.ToString())
                ]);
                await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, responseHeaders, cts.Token);
                await Http3Frame.WriteAsync(stream, Http3FrameType.Data, body, cts.Token);
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

internal static class QuicHttp3LoadGenerator
{
    public static async Task WarmupAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, CancellationToken cancellationToken)
    {
        await RunAsync(proxyEndPoint, sniHost, authority, concurrency, duration, collectLatency: false,
            cancellationToken);
    }

    public static Task<LoadResult> RunAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, CancellationToken cancellationToken = default) =>
        RunAsync(proxyEndPoint, sniHost, authority, concurrency, duration, collectLatency: true, cancellationToken);

    private static async Task<LoadResult> RunAsync(IPEndPoint proxyEndPoint, string sniHost, string authority,
        int concurrency, TimeSpan duration, bool collectLatency, CancellationToken cancellationToken)
    {
        var ok = 0L;
        var errors = 0L;
        var latencies = collectLatency ? new ConcurrentBag<double>() : null;
        string? firstError = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);

        var connectionCount = Math.Clamp(concurrency / 8, 1, 8);
        var connections = new QuicConnection?[connectionCount];
        var connectionLocks = new object[connectionCount];
        for (var i = 0; i < connectionCount; i++)
            connectionLocks[i] = new object();

        try
        {
            for (var c = 0; c < connectionCount; c++)
            {
                connections[c] = await ConnectAsync(proxyEndPoint, sniHost, cts.Token);
                await OpenControlAsync(connections[c]!, cts.Token);
                DrainInbound(connections[c]!, cts.Token);
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
                        QuicConnection? connection;
                        lock (connectionLocks[slot])
                            connection = connections[slot];

                        if (connection == null)
                        {
                            try
                            {
                                connection = await ConnectAsync(proxyEndPoint, sniHost, cts.Token);
                                await OpenControlAsync(connection, cts.Token);
                                DrainInbound(connection, cts.Token);
                                lock (connectionLocks[slot])
                                    connections[slot] = connection;
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
                            var status = await SendGetAsync(connection, authority, "/", cts.Token);
                            if (status is >= 200 and < 300)
                                Interlocked.Increment(ref ok);
                            else
                            {
                                Interlocked.Increment(ref errors);
                                if (firstError == null)
                                {
                                    firstError = "non-2xx status=" + status;
                                    ProbeLog.Error($"  [quic-h3] {firstError}");
                                    // #region agent log
                                    DebugSessionLog.Write("C", "QuicHttp3LoadGenerator", "first-error",
                                        new { error = firstError, endpoint = proxyEndPoint.ToString(), authority });
                                    // #endregion
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
                                // #region agent log
                                DebugSessionLog.Write("C", "QuicHttp3LoadGenerator", "first-error",
                                    new { error = firstError, endpoint = proxyEndPoint.ToString() });
                                // #endregion
                            }

                            Interlocked.Increment(ref errors);
                            if (requestSw != null)
                            {
                                requestSw.Stop();
                                latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                            }

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
        return await QuicConnection.ConnectAsync(options, cancellationToken);
    }

    private static async Task OpenControlAsync(QuicConnection connection, CancellationToken cancellationToken)
    {
        var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
        await control.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, cancellationToken);
        var settings = new Http3Settings();
        settings.SetQpackMaxTableCapacity(0);
        settings.SetQpackBlockedStreams(0);
        await Http3Frame.WriteAsync(control, Http3FrameType.Settings, settings.Serialize(), cancellationToken);
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

    private static async Task<int> SendGetAsync(QuicConnection connection, string authority, string path,
        CancellationToken cancellationToken)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, cancellationToken);

        var headers = QpackEncoder.Encode(
        [
            (":method", "GET"),
            (":scheme", "https"),
            (":authority", authority),
            (":path", path)
        ]);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, headers, cancellationToken);
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
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, cancellationToken);
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
