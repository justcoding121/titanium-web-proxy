using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>Which protocol legs participate in bridge response compression.</summary>
    internal readonly record struct BridgeCompressionShape(bool OriginLegIsHttp1, bool ClientIsHttp2, bool ClientIsHttp3);

    /// <summary>
    ///     Buffered gzip / Brotli / zstd for reverse-proxy bridges (H2/H3 origins and HTTP/3 clients).
    /// </summary>
    internal async Task TryCompressBridgeBufferedResponseAsync(SessionEventArgs args,
        BridgeCompressionShape shape, byte[]? knownBody = null)
    {
        if (CompressionPolicy is not { Streaming: false } policy)
            return;

        var request = args.HttpClient.Request;
        var response = args.HttpClient.Response;
        if (!IsBufferedBridgeCandidate(args, shape, knownBody))
            return;

        var selectResponse = response;
        if (!shape.OriginLegIsHttp1 && response.OriginalHasBody)
        {
            selectResponse = SnapshotForSelection(response);
        }

        if (!policy.TrySelect(request, selectResponse, out var algorithm))
            return;

        if (await ReadCompressionBodyAsync(args, response, selectResponse, knownBody).ConfigureAwait(false) is not { } body)
            return;

        var compressed = policy.Compress(algorithm, body);
        if (compressed.Length >= body.Length)
            return;

        ResponseCompressionPolicy.Apply(response, algorithm, compressed);
    }

    /// <summary>
    ///     Streaming compression on bridge paths. Returns null when ineligible or when compression is off.
    /// </summary>
    internal StreamingCompressionPlan? TryBeginBridgeStreamingCompression(SessionEventArgs args,
        BridgeCompressionShape shape)
    {
        if (CompressionPolicy is not { Streaming: true } policy)
            return null;

        var request = args.HttpClient.Request;
        var response = args.HttpClient.Response;

        if (!IsStreamingBridgeCandidate(args, shape))
            return null;

        var selectResponse = shape.OriginLegIsHttp1 ? response : SnapshotForSelection(response);
        if (!policy.TrySelectStreaming(request, selectResponse, out var algorithm))
            return null;

        var originChunked = shape.OriginLegIsHttp1 ? response.IsChunked : response.OriginalIsChunked;
        var originLength = shape.OriginLegIsHttp1 ? response.ContentLength : response.OriginalContentLength;
        var plan = new StreamingCompressionPlan(algorithm, originChunked, originLength);
        ResponseCompressionPolicy.ApplyStreaming(response, algorithm);
        if (shape.ClientIsHttp2 || shape.ClientIsHttp3)
            response.IsChunked = false;
        return plan;
    }

    private bool IsBufferedBridgeCandidate(SessionEventArgs args, BridgeCompressionShape shape, byte[]? knownBody)
    {
        var response = args.HttpClient.Response;
        if (response.Locked || response.IsSynthetic || response.Http3RawBodyDrain != null)
            return false;

        if (response.StreamBodyWriter != null && knownBody is null)
            return false;

        if (!shape.OriginLegIsHttp1 && response.HttpVersion.Major >= 2 && !response.OriginalHasBody
            && !response.HasBody && knownBody is null)
            return false;

        if (!ClientLegAcceptsCompression(args.HttpClient.Request, shape))
            return false;

        if (ShouldCallBeforeResponseBodyWrite())
            return false;

        return !ResponseCompressionPolicy.IsRouteOff(args.MatchedRoute);
    }

    private bool IsStreamingBridgeCandidate(SessionEventArgs args, BridgeCompressionShape shape)
    {
        var request = args.HttpClient.Request;
        var response = args.HttpClient.Response;
        if (response.IsBodyRead || response.IsSynthetic || response.Http3RawBodyDrain != null)
            return false;

        if (!response.OriginalHasBody && !response.HasBody)
            return false;

        if (!shape.ClientIsHttp2 && !shape.ClientIsHttp3 && request.HttpVersion == HttpHeader.Version10)
            return false;

        if (!ClientLegAcceptsCompression(request, shape))
            return false;

        if (ShouldCallBeforeResponseBodyWrite())
            return false;

        return !ResponseCompressionPolicy.IsRouteOff(args.MatchedRoute);
    }

    private static bool ClientLegAcceptsCompression(Request request, BridgeCompressionShape shape)
    {
        if (shape.ClientIsHttp3 && request.HttpVersion.Major < 3)
            return false;

        return shape.ClientIsHttp3 || request.HttpVersion.Major < 3;
    }

    /// <summary>
    ///     HTTP/3 client path: buffered or streaming compression after <c>BeforeResponse</c>, before QPACK encode.
    /// </summary>
    internal Task ApplyHttp3ClientResponseCompressionAsync(SessionEventArgs args) =>
        CompressionPolicy is null || args.IsFastPath
            ? Task.CompletedTask
            : ApplyHttp3ClientResponseCompressionCoreAsync(args);

    private async Task ApplyHttp3ClientResponseCompressionCoreAsync(SessionEventArgs args)
    {
        var shape = new BridgeCompressionShape(OriginIsHttp1(args), ClientIsHttp2: false, ClientIsHttp3: true);
        var response = args.HttpClient.Response;
        if (response.Locked || response.Http3RawBodyDrain != null)
            return;

        if (CompressionPolicy is { Streaming: false })
        {
            await TryCompressBridgeBufferedResponseAsync(args, shape).ConfigureAwait(false);
            return;
        }

        var plan = TryBeginBridgeStreamingCompression(args, shape);
        if (plan is null)
            return;

        var policy = CompressionPolicy!;
        var originWriter = response.StreamBodyWriter;
        response.StreamBodyWriter = async (sink, ct) =>
        {
            if (originWriter is not null)
            {
                await using var origin = new StreamBodyWriterPumpStream(originWriter, ct);
                var buffer = BufferPool.GetBuffer(StreamingResponseRelay.ReadBufferBytes);
                try
                {
                    await StreamingResponseRelay.RelayAsync(origin, sink, plan.Value.Algorithm, policy.Level, buffer,
                        plan.Value.OriginChunked ? -1 : plan.Value.OriginLength, args.OnDataReceived, null, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    BufferPool.ReturnBuffer(buffer);
                }

                return;
            }

            await RelayCompressedBodyAsync(args, policy, plan.Value, sink, null, ct).ConfigureAwait(false);
        };
    }

    private static Response SnapshotForSelection(Response response)
    {
        var snap = new Response
        {
            HttpVersion = response.HttpVersion,
            StatusCode = response.StatusCode
        };
        foreach (var header in response.Headers)
            snap.Headers.AddHeader(header);

        // Stamp the origin's framing over the copied headers (the setters replace in place). Setting the
        // framing first and copying the headers after it duplicates Content-Length / Transfer-Encoding, and
        // a duplicated header reads back as "no framing": the length becomes -1, so buffered selection
        // rejects every bridged response and streaming selection mistakes a chunked body for unknown framing.
        snap.ContentLength = response.OriginalContentLength;
        snap.IsChunked = response.OriginalIsChunked;
        return snap;
    }

    /// <summary>HTTP/1.1 client to HTTP/2 origin (<see cref="Http11ToHttp2BridgeHandler" />).</summary>
    internal async Task ApplyH1ToH2BridgeResponseCompressionAsync(SessionEventArgs args,
        Http2.Http2OriginExchange exchange, CancellationToken cancellationToken)
    {
        if (CompressionPolicy is null || args.IsFastPath)
            return;

        var response = args.HttpClient.Response;
        if (response.Locked)
            return;

        var shape = new BridgeCompressionShape(OriginLegIsHttp1: false, ClientIsHttp2: false, ClientIsHttp3: false);
        if (CompressionPolicy is { Streaming: false })
            return;

        var plan = TryBeginBridgeStreamingCompression(args, shape);
        if (plan is null)
            return;

        var policy = CompressionPolicy;
        var originWriter = response.StreamBodyWriter;
        response.StreamBodyWriter = async (sink, ct) =>
        {
            if (originWriter is not null)
            {
                await using var origin = new StreamBodyWriterPumpStream(originWriter, ct);
                var buffer = BufferPool.GetBuffer(StreamingResponseRelay.ReadBufferBytes);
                try
                {
                    await StreamingResponseRelay.RelayAsync(origin, sink, plan.Value.Algorithm, policy.Level, buffer,
                        plan.Value.OriginChunked ? -1 : plan.Value.OriginLength, args.OnDataReceived, null, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    BufferPool.ReturnBuffer(buffer);
                }

                return;
            }

            await RelayCompressedBodyAsync(args, policy, plan.Value, sink, null, ct).ConfigureAwait(false);
        };
    }

    private static bool OriginIsHttp1(SessionEventArgs args) =>
        args.HttpClient.HasRecordedOriginHttpVersion
            ? args.HttpClient.OriginHttpVersion.Major == 1
            : args.HttpClient.Response.HttpVersion.Major == 1;

    private async Task<byte[]?> ReadCompressionBodyAsync(
        SessionEventArgs args, Response response, Response selectResponse, byte[]? knownBody)
    {
        if (knownBody is not null)
            return knownBody;

        if (response.IsBodyRead || response.BodyAvailable)
            return response.Body;

        var budget = args.MaxBufferedBodyBytes ?? MaxBufferedBodyBytes;
        if (budget > 0 && selectResponse.ContentLength > budget)
            return null;

        return await args.GetResponseBody(args.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads a response by draining <see cref="Response.StreamBodyWriter" /> into sequential reads.</summary>
internal sealed class StreamBodyWriterPumpStream : Stream
{
    private readonly Func<Stream, CancellationToken, Task> _writer;
    private readonly CancellationToken _cancellationToken;
    private readonly System.Threading.Channels.Channel<byte[]> _channel =
        System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(4)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    private Task? _pump;
    private byte[]? _current;
    private int _offset;

    internal StreamBodyWriterPumpStream(Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken)
    {
        _writer = writer;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Run the writer off the reader's call stack: a synchronous sink.Write that fills the bounded channel
        // blocks until the reader drains it, which would deadlock if the writer ran inline inside this call.
        _pump ??= Task.Run(PumpAsync, CancellationToken.None);
        while (true)
        {
            if (_current != null && _offset < _current.Length)
            {
                var n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            // End of body is channel completion (never a zero-length chunk). A writer fault completes the channel
            // with its exception, so WaitToReadAsync rethrows it after the queued chunks drain instead of
            // presenting a truncated body as a clean EOF.
            if (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && _channel.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
                continue;
            }

            return 0;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await _writer(new ChannelWriteStream(_channel.Writer, _cancellationToken), _cancellationToken)
                .ConfigureAwait(false);
            _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _channel.Writer.TryComplete();
        base.Dispose(disposing);
    }

    private sealed class ChannelWriteStream : Stream
    {
        private readonly System.Threading.Channels.ChannelWriter<byte[]> _channelWriter;

        private readonly CancellationToken _cancellationToken;

        internal ChannelWriteStream(System.Threading.Channels.ChannelWriter<byte[]> channelWriter,
            CancellationToken cancellationToken)
        {
            _channelWriter = channelWriter;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // A bounded channel's TryWrite returns false when it is full, which would silently drop the chunk.
        // Block on the async write instead so a synchronous writer gets back-pressure like an async one.
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return;

            _channelWriter.WriteAsync(buffer.AsSpan(offset, count).ToArray(), _cancellationToken).AsTask()
                .GetAwaiter().GetResult();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // An empty chunk carries no data; queueing it would only add a no-op read.
            if (buffer.IsEmpty)
                return ValueTask.CompletedTask;

            return _channelWriter.WriteAsync(buffer.ToArray(), cancellationToken);
        }
    }
}
