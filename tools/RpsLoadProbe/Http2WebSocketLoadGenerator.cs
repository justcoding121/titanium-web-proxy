using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Minimal HTTP/2 client that opens an RFC 8441 extended CONNECT (<c>:protocol=websocket</c>)
/// tunnel to a reverse proxy, then measures WebSocket echo round-trips over DATA frames.
/// </summary>
internal static class Http2WebSocketLoadGenerator
{
    private static readonly byte[] PingPayload = "twp-ws-h2"u8.ToArray();
    private static readonly byte[] Preface = Http2Helper.ConnectionPreface;

    public static async Task<LoadResult> RunAsync(LoadRequestOptions options, int concurrency, TimeSpan duration,
        bool collectLatency, CancellationToken cancellationToken)
    {
        var targets = EmbeddedLoadGenerator.ResolveTargets(options);
        var ok = 0L;
        var errors = 0L;
        var latencies = collectLatency ? new ConcurrentBag<double>() : null;
        string? firstError = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);

        var sw = Stopwatch.StartNew();
        var workers = new Task[Math.Max(1, concurrency)];
        for (var i = 0; i < workers.Length; i++)
        {
            var workerId = i;
            workers[i] = Task.Run(async () =>
            {
                var rr = workerId;
                Session? session = null;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var target = targets[rr++ % targets.Count];
                        var requestSw = collectLatency ? Stopwatch.StartNew() : null;
                        try
                        {
                            if (session == null || !session.IsOpen)
                            {
                                session?.Dispose();
                                session = await Session.ConnectAsync(target, cts.Token);
                            }

                            await session.EchoOnceAsync(PingPayload, cts.Token);
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
                                ProbeLog.Error($"  [ws-h2] first error: {firstError}");
                            }

                            Interlocked.Increment(ref errors);
                            if (requestSw != null)
                            {
                                requestSw.Stop();
                                latencies!.Add(requestSw.Elapsed.TotalMilliseconds);
                            }

                            try { session?.Dispose(); } catch { /* ignore */ }
                            session = null;
                        }
                    }
                }
                finally
                {
                    try { session?.Dispose(); } catch { /* ignore */ }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers);
        sw.Stop();

        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var total = ok + errors;
        var samples = latencies?.ToArray() ?? [];
        Array.Sort(samples);
        return new LoadResult(
            Generator: "dotnet-http2-websocket",
            Concurrency: concurrency,
            DurationSeconds: elapsed,
            Ok: ok,
            Errors: errors,
            Rps: ok / elapsed,
            ErrorRatePercent: total == 0 ? 100 : 100.0 * errors / total,
            P50Ms: EmbeddedLoadGenerator.Percentile(samples, 0.50),
            P99Ms: EmbeddedLoadGenerator.Percentile(samples, 0.99),
            MaxMs: samples.Length == 0 ? 0 : samples[^1],
            NegotiatedVersionHint: "h2-ws-8441");
    }

    private sealed class Session : IDisposable
    {
        private readonly TcpClient tcp;
        private readonly SslStream ssl;
        private readonly Stream stream;
        private readonly Titanium.Web.Proxy.Http2.Hpack.Encoder encoder = new(4096);
        private readonly Titanium.Web.Proxy.Http2.Hpack.Decoder decoder = new(4096, 8192);
        private int nextStreamId = 1;
        private int activeStreamId;
        private bool tunnelOpen;

        private Session(TcpClient tcp, SslStream ssl)
        {
            this.tcp = tcp;
            this.ssl = ssl;
            stream = ssl;
        }

        public bool IsOpen => tunnelOpen && tcp.Connected;

        public static async Task<Session> ConnectAsync(Uri target, CancellationToken cancellationToken)
        {
            if (!string.Equals(target.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RFC 8441 probe requires https:// reverse listen.");

            var tcp = new TcpClient();
            await tcp.ConnectAsync(target.Host, target.Port, cancellationToken);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(target.Host) ? "localhost" : target.Host,
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                EnabledSslProtocols = SslProtocols.None
            }, cancellationToken);

            if (ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                throw new InvalidOperationException(
                    $"ALPN negotiated '{ssl.NegotiatedApplicationProtocol}' (want h2).");

            var session = new Session(tcp, ssl);
            await session.stream.WriteAsync(Preface, cancellationToken);
            // Empty SETTINGS + ACK peer SETTINGS as they arrive.
            await session.WriteFrameAsync(Http2FrameType.Settings, 0, 0, [], cancellationToken);
            await session.OpenTunnelAsync(target, cancellationToken);
            return session;
        }

        private async Task OpenTunnelAsync(Uri target, CancellationToken cancellationToken)
        {
            // Drain peer SETTINGS (and ACK) before opening the stream.
            await DrainUntilReadyAsync(cancellationToken);

            var streamId = nextStreamId;
            nextStreamId += 2;
            activeStreamId = streamId;

            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var authority = target.IsDefaultPort ? target.Host : $"{target.Host}:{target.Port}";
            var headerBlock = EncodeHeaders(
            [
                (":method", "CONNECT"),
                (":protocol", "websocket"),
                // Cleartext H1 origin behind TLS-terminating reverse (ForwardCleartext).
                (":scheme", "http"),
                (":authority", authority),
                (":path", "/ws")
            ],
            [
                ("sec-websocket-version", "13"),
                ("sec-websocket-key", wsKey)
            ]);

            await WriteHeaderBlockAsync(streamId, headerBlock, endStream: false, cancellationToken);

            string? status = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(cancellationToken);
                await MaybeAckSettingsAsync(frame, cancellationToken);
                if (frame.Type == Http2FrameType.Headers && frame.StreamId == streamId)
                {
                    var headers = DecodeHeaders(frame.Payload);
                    status = headers.FirstOrDefault(h => h.Name == ":status").Value;
                    break;
                }

                if (frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId)
                    throw new InvalidOperationException("RST_STREAM opening RFC 8441 tunnel.");
            }

            if (status != "200")
                throw new InvalidOperationException($"RFC 8441 tunnel status {status ?? "(none)"} (want 200).");

            tunnelOpen = true;
        }

        public async Task EchoOnceAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var frame = BuildMaskedBinaryFrame(payload);
            await WriteFrameAsync(Http2FrameType.Data, activeStreamId, 0, frame, cancellationToken);

            var received = new List<byte>(frame.Length);
            while (received.Count < 2 && !cancellationToken.IsCancellationRequested)
            {
                var f = await ReadFrameAsync(cancellationToken);
                await MaybeAckSettingsAsync(f, cancellationToken);
                if (f.Type == Http2FrameType.Data && f.StreamId == activeStreamId && f.Payload.Length > 0)
                    received.AddRange(f.Payload);
                else if (f.Type == Http2FrameType.RstStream && f.StreamId == activeStreamId)
                    throw new InvalidOperationException("RST_STREAM during WebSocket echo.");
            }

            // Server frames are unmasked; require at least opcode+len then payload bytes.
            if (received.Count < 2 + payload.Length)
            {
                while (received.Count < 2 + payload.Length && !cancellationToken.IsCancellationRequested)
                {
                    var f = await ReadFrameAsync(cancellationToken);
                    await MaybeAckSettingsAsync(f, cancellationToken);
                    if (f.Type == Http2FrameType.Data && f.StreamId == activeStreamId && f.Payload.Length > 0)
                        received.AddRange(f.Payload);
                }
            }

            if (received.Count < 2 + payload.Length)
                throw new InvalidOperationException("short WebSocket echo over h2 DATA");
        }

        private async Task DrainUntilReadyAsync(CancellationToken cancellationToken)
        {
            // Read until we see peer SETTINGS, ACK it, then proceed (non-blocking best-effort).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(linked.Token);
                    await MaybeAckSettingsAsync(frame, cancellationToken);
                    if (frame.Type == Http2FrameType.Settings && (frame.Flags & Http2FrameFlag.Ack) == 0)
                        return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Peer may have already ACKed; continue.
            }
        }

        private async Task MaybeAckSettingsAsync(Frame frame, CancellationToken cancellationToken)
        {
            if (frame.Type == Http2FrameType.Settings && (frame.Flags & Http2FrameFlag.Ack) == 0)
                await WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack, [], cancellationToken);
            else if (frame.Type == Http2FrameType.Ping && (frame.Flags & Http2FrameFlag.Ack) == 0)
                await WriteFrameAsync(Http2FrameType.Ping, 0, Http2FrameFlag.Ack, frame.Payload, cancellationToken);
            else if (frame.Type == Http2FrameType.WindowUpdate)
            {
                // Ignore peer window updates; we keep payloads tiny.
            }
        }

        private byte[] EncodeHeaders(IEnumerable<(string Name, string Value)> pseudo,
            IEnumerable<(string Name, string Value)> headers)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            foreach (var (name, value) in pseudo)
            {
                encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString(), false,
                    HpackUtil.IndexType.None, false);
            }

            foreach (var (name, value) in headers)
                encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString());
            return ms.ToArray();
        }

        private List<(string Name, string Value)> DecodeHeaders(byte[] compressed)
        {
            var listener = new RecordingHeaderListener();
            decoder.Decode(compressed, listener);
            decoder.EndHeaderBlock();
            return listener.Headers;
        }

        private sealed class RecordingHeaderListener : IHeaderListener
        {
            public List<(string Name, string Value)> Headers { get; } = [];

            public void AddHeader(ByteString name, ByteString value, bool sensitive) =>
                Headers.Add((name.GetString(), value.GetString()));
        }

        private async Task WriteHeaderBlockAsync(int streamId, byte[] compressed, bool endStream,
            CancellationToken cancellationToken)
        {
            var flags = Http2FrameFlag.EndHeaders;
            if (endStream) flags |= Http2FrameFlag.EndStream;
            await WriteFrameAsync(Http2FrameType.Headers, streamId, flags, compressed, cancellationToken);
        }

        private async Task WriteFrameAsync(Http2FrameType type, int streamId, Http2FrameFlag flags, byte[] payload,
            CancellationToken cancellationToken)
        {
            var header = new byte[9];
            var length = payload.Length;
            header[0] = (byte)((length >> 16) & 0xff);
            header[1] = (byte)((length >> 8) & 0xff);
            header[2] = (byte)(length & 0xff);
            header[3] = (byte)type;
            header[4] = (byte)flags;
            header[5] = (byte)((streamId >> 24) & 0x7f);
            header[6] = (byte)((streamId >> 16) & 0xff);
            header[7] = (byte)((streamId >> 8) & 0xff);
            header[8] = (byte)(streamId & 0xff);
            await stream.WriteAsync(header, cancellationToken);
            if (length > 0)
                await stream.WriteAsync(payload.AsMemory(0, length), cancellationToken);
        }

        private async Task<Frame> ReadFrameAsync(CancellationToken cancellationToken)
        {
            var header = new byte[9];
            await ReadExactAsync(stream, header, cancellationToken);
            var length = (header[0] << 16) + (header[1] << 8) + header[2];
            var type = (Http2FrameType)header[3];
            var flags = (Http2FrameFlag)header[4];
            var streamId = ((header[5] & 0x7f) << 24) + (header[6] << 16) + (header[7] << 8) + header[8];
            var payload = new byte[length];
            if (length > 0)
                await ReadExactAsync(stream, payload, cancellationToken);
            return new Frame(type, streamId, flags, payload);
        }

        private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var n = await s.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (n == 0)
                    throw new EndOfStreamException("peer closed HTTP/2 connection");
                offset += n;
            }
        }

        private static byte[] BuildMaskedBinaryFrame(byte[] payload)
        {
            var mask = RandomNumberGenerator.GetBytes(4);
            var frame = new byte[2 + 4 + payload.Length];
            frame[0] = 0x82; // FIN + binary
            frame[1] = (byte)(0x80 | payload.Length); // MASK + length (<126)
            Buffer.BlockCopy(mask, 0, frame, 2, 4);
            for (var i = 0; i < payload.Length; i++)
                frame[6 + i] = (byte)(payload[i] ^ mask[i % 4]);
            return frame;
        }

        public void Dispose()
        {
            tunnelOpen = false;
            try { ssl.Dispose(); } catch { /* ignore */ }
            try { tcp.Dispose(); } catch { /* ignore */ }
        }

        private readonly record struct Frame(Http2FrameType Type, int StreamId, Http2FrameFlag Flags, byte[] Payload);
    }
}
