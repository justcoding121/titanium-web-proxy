using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
///     Streaming response compression (<c>responseCompression.mode: streaming</c>) and bounded request
///     decompression (<c>requestDecompression</c>): eligibility, header rewrite, the relay's flush, backpressure and
///     abort behavior, and the decompression limits.
/// </summary>
[TestClass]
public class StreamingCompressionTests
{
    private static ResponseCompressionPolicy Streaming(ResponseCompressionConfig? config = null) =>
        ResponseCompressionPolicy.Create(config ?? new ResponseCompressionConfig { Enabled = true, Mode = "streaming" })!;

    private static Request NewRequest(string acceptEncoding = "br, gzip", string method = "GET")
    {
        var request = new Request { Method = method, HttpVersion = HttpHeader.Version11 };
        if (acceptEncoding.Length > 0)
        {
            request.Headers.AddHeader("Accept-Encoding", acceptEncoding);
        }

        return request;
    }

    private static Response ChunkedResponse(string contentType = "text/html")
    {
        var response = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        response.Headers.AddHeader("Content-Type", contentType);
        response.Headers.AddHeader("Transfer-Encoding", "chunked");
        return response;
    }

    private static Response SizedResponse(long length, string contentType = "text/html")
    {
        var response = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        response.Headers.AddHeader("Content-Type", contentType);
        response.Headers.AddHeader("Content-Length", length.ToString());
        return response;
    }

    // ---- mode ------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null, true, false)]
    [DataRow("", true, false)]
    [DataRow("buffered", true, false)]
    [DataRow("BUFFERED", true, false)]
    [DataRow("streaming", true, true)]
    [DataRow(" Streaming ", true, true)]
    [DataRow("chunked", false, false)]
    public void Mode_Parses(string? mode, bool valid, bool streaming)
    {
        Assert.AreEqual(valid, ResponseCompressionPolicy.TryParseMode(mode, out var parsed));
        Assert.AreEqual(streaming, parsed);
    }

    [TestMethod]
    public void Mode_DefaultsToBuffered()
    {
        Assert.IsFalse(ResponseCompressionPolicy.Create(new ResponseCompressionConfig { Enabled = true })!.Streaming);
        Assert.IsTrue(Streaming().Streaming);
    }

    // ---- eligibility -----------------------------------------------------------------------------------

    [TestMethod]
    public void Streaming_AcceptsChunkedAndCloseDelimitedBodies()
    {
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(), ChunkedResponse(), out var algorithm));
        Assert.AreEqual(HttpCompression.Brotli, algorithm);

        var closeDelimited = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        closeDelimited.Headers.AddHeader("Content-Type", "text/plain");
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(), closeDelimited, out _));
    }

    [TestMethod]
    public void Streaming_HasNoUpperBound_ButBufferedStillDoes()
    {
        var large = SizedResponse(512L * 1024 * 1024);
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(), large, out _));
        Assert.IsFalse(Streaming().TrySelect(NewRequest(), large, out _));
    }

    [TestMethod]
    public void Streaming_MinBytesAppliesOnlyToAKnownLength()
    {
        Assert.IsFalse(Streaming().TrySelectStreaming(NewRequest(), SizedResponse(1023), out _));
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(), SizedResponse(1024), out _));
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(), ChunkedResponse(), out _));
    }

    public static IEnumerable<object[]> StreamingIneligibleCases()
    {
        yield return Case("status 206", (_, r) => r.StatusCode = 206);
        yield return Case("status 304", (_, r) => r.StatusCode = 304);
        yield return Case("status 404", (_, r) => r.StatusCode = 404);
        yield return Case("HEAD", (q, _) => q.Method = "HEAD");
        yield return Case("Range", (q, _) => q.Headers.AddHeader("Range", "bytes=0-9"));
        yield return Case("Content-Range", (_, r) => r.Headers.AddHeader("Content-Range", "bytes 0-9/100"));
        yield return Case("Content-Encoding", (_, r) => r.Headers.AddHeader("Content-Encoding", "gzip"));
        yield return Case("no-transform", (_, r) => r.Headers.AddHeader("Cache-Control", "no-transform"));
        yield return Case("event-stream", (_, r) => SetType(r, "text/event-stream"));
        yield return Case("grpc", (_, r) => SetType(r, "application/grpc"));
        yield return Case("png", (_, r) => SetType(r, "image/png"));
        yield return Case("no content type", (_, r) => r.Headers.RemoveHeader("Content-Type"));
        yield return Case("websocket", (q, _) =>
        {
            q.Headers.AddHeader("Upgrade", "websocket");
            q.Headers.AddHeader("Connection", "Upgrade");
        });
        yield return Case("no Accept-Encoding", (q, _) => q.Headers.RemoveHeader("Accept-Encoding"));
        yield return Case("identity only", (q, _) =>
        {
            q.Headers.RemoveHeader("Accept-Encoding");
            q.Headers.AddHeader("Accept-Encoding", "identity");
        });
        yield return Case("br;q=0, gzip;q=0", (q, _) =>
        {
            q.Headers.RemoveHeader("Accept-Encoding");
            q.Headers.AddHeader("Accept-Encoding", "br;q=0, gzip;q=0");
        });
    }

    private static object[] Case(string name, Action<Request, Response> mutate) => [name, mutate];

    private static void SetType(Response response, string type)
    {
        response.Headers.RemoveHeader("Content-Type");
        response.Headers.AddHeader("Content-Type", type);
    }

    [TestMethod]
    [DynamicData(nameof(StreamingIneligibleCases))]
    public void Streaming_EachRuleRefuses(string name, Action<Request, Response> mutate)
    {
        var request = NewRequest();
        var response = ChunkedResponse();
        mutate(request, response);

        Assert.IsFalse(Streaming().TrySelectStreaming(request, response, out _), name);
    }

    [TestMethod]
    public void Wasm_IsCompressedOnlyWhenListed()
    {
        var wasm = ChunkedResponse("application/wasm");
        Assert.IsFalse(Streaming().TrySelectStreaming(NewRequest(), wasm, out _));

        var listed = Streaming(new ResponseCompressionConfig
        {
            Enabled = true,
            Mode = "streaming",
            MimeTypes = ["text/*", "application/wasm"],
        });
        Assert.IsTrue(listed.TrySelectStreaming(NewRequest(), ChunkedResponse("application/wasm"), out _));
    }

    // ---- q-values --------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("br, gzip", "br")]
    [DataRow("gzip, br", "br")]
    [DataRow("gzip;q=1, br;q=0.5", "gzip")]
    [DataRow("gzip;q=0.2, br;q=0.9", "br")]
    [DataRow("br;q=0.8, gzip;q=0.8", "br")]
    [DataRow("*;q=0.1, gzip;q=0.5", "gzip")]
    [DataRow("br;q=0, *;q=0.3", "gzip")]
    public void AcceptEncoding_TheHighestWeightWins(string accept, string expected)
    {
        Assert.IsTrue(Streaming().TrySelectStreaming(NewRequest(accept), ChunkedResponse(), out var algorithm));
        Assert.AreEqual(expected == "br" ? HttpCompression.Brotli : HttpCompression.Gzip, algorithm);
    }

    // ---- header rewrite --------------------------------------------------------------------------------

    [TestMethod]
    public void ApplyStreaming_RewritesTheHeaders()
    {
        var response = SizedResponse(5000);
        response.Headers.AddHeader("ETag", "\"v1\"");
        response.Headers.AddHeader("Vary", "Origin");

        ResponseCompressionPolicy.ApplyStreaming(response, HttpCompression.Gzip);

        Assert.AreEqual("gzip", response.ContentEncoding);
        Assert.IsFalse(response.Headers.HeaderExists("Content-Length"));
        Assert.IsTrue(response.IsChunked);
        Assert.AreEqual("W/\"v1\"", response.Headers.GetFirstHeader("ETag")!.Value);
        var vary = string.Join(",", response.Headers.GetHeaders("Vary")!.Select(h => h.Value));
        StringAssert.Contains(vary, "Origin");
        StringAssert.Contains(vary, "Accept-Encoding");
    }

    [TestMethod]
    public void ApplyStreaming_DoesNotDuplicateVary()
    {
        var response = ChunkedResponse();
        response.Headers.AddHeader("Vary", "accept-encoding");
        ResponseCompressionPolicy.ApplyStreaming(response, HttpCompression.Brotli);
        Assert.AreEqual(1, response.Headers.GetHeaders("Vary")!.Count);
        Assert.AreEqual("br", response.ContentEncoding);
    }

    // ---- relay: round trip, flush, backpressure, abort ------------------------------------------------

    private static HttpCompression Parse(string name) => name == "br" ? HttpCompression.Brotli : HttpCompression.Gzip;

    private static byte[] Decode(HttpCompression algorithm, byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using Stream decoder = algorithm == HttpCompression.Brotli
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Text(int length) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("<li>streaming compression</li>\n", length / 31 + 1)))
            [..length];

    /// <summary>Hands out the given pieces one per read, optionally waiting on a gate before a piece.</summary>
    private sealed class PieceStream : Stream
    {
        private readonly IEnumerator<byte[]> pieces;
        private readonly Func<int, Task>? beforePiece;
        private int index;

        public PieceStream(IEnumerable<byte[]> pieces, Func<int, Task>? beforePiece = null)
        {
            this.pieces = pieces.GetEnumerator();
            this.beforePiece = beforePiece;
        }

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pieces.MoveNext())
            {
                return 0;
            }

            if (beforePiece is not null)
            {
                await beforePiece(index);
            }

            index++;
            Reads++;
            var piece = pieces.Current;
            piece.CopyTo(buffer);
            return piece.Length;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Records each write the relay makes; can block writes to model a slow client.</summary>
    private sealed class RecordingSink : Stream
    {
        private readonly MemoryStream all = new();
        private readonly Func<int, Task>? beforeWrite;

        public RecordingSink(Func<int, Task>? beforeWrite = null)
        {
            this.beforeWrite = beforeWrite;
        }

        public List<int> Writes { get; } = [];

        public byte[] Bytes => all.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (beforeWrite is not null)
            {
                await beforeWrite(Writes.Count);
            }

            Writes.Add(buffer.Length);
            all.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    public async Task Relay_RoundTrips(string name)
    {
        var algorithm = Parse(name);
        var body = Text(300_000);
        var origin = new PieceStream(body.Chunk(7_001));
        var sink = new RecordingSink();
        var buffer = new byte[StreamingResponseRelay.ReadBufferBytes];

        var done = await StreamingResponseRelay.RelayAsync(origin, sink, algorithm, CompressionLevel.Fastest, buffer,
            body.Length, null, null, CancellationToken.None);

        Assert.IsTrue(done);
        CollectionAssert.AreEqual(body, Decode(algorithm, sink.Bytes));
        Assert.IsTrue(sink.Bytes.Length < body.Length / 4, "compressed");
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    public async Task Relay_FlushesOnEveryUpstreamRead(string name)
    {
        var algorithm = Parse(name);
        // The next origin read is held back until the sink has seen output for the previous piece: only a
        // flush after each read lets the relay get that far.
        var firstOutput = new TaskCompletionSource();
        var sinkWrites = 0;
        var sink = new RecordingSink(_ =>
        {
            if (Interlocked.Increment(ref sinkWrites) == 1)
            {
                firstOutput.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var pieces = new[] { Text(900), Text(900), Text(900) };
        var origin = new PieceStream(pieces, async index =>
        {
            if (index == 1)
            {
                await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });

        var done = await StreamingResponseRelay.RelayAsync(origin, sink, algorithm, CompressionLevel.Fastest,
            new byte[8192], -1, null, null, CancellationToken.None);

        Assert.IsTrue(done);
        Assert.IsTrue(sink.Writes.Count >= pieces.Length, $"one sink write per upstream read at least: {sink.Writes.Count}");
        CollectionAssert.AreEqual(pieces.SelectMany(p => p).ToArray(), Decode(algorithm, sink.Bytes));
    }

    [TestMethod]
    public async Task Relay_BackpressureKeepsTheOriginOneStepAhead()
    {
        // A client that never reads: the sink blocks. The origin is a 1 TiB virtual body; if the relay buffered
        // ahead it would read far more than one piece before the sink returns.
        var release = new TaskCompletionSource();
        var sinkEntered = new TaskCompletionSource();
        var sink = new RecordingSink(async _ =>
        {
            sinkEntered.TrySetResult();
            await release.Task;
        });
        var piece = Text(16 * 1024);
        var origin = new PieceStream(Endless(piece));
        using var cts = new CancellationTokenSource();
        var relay = Task.Run(() => StreamingResponseRelay.RelayAsync(origin, sink, HttpCompression.Gzip,
            CompressionLevel.Fastest, new byte[32 * 1024], -1, null, null, cts.Token));

        await sinkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);

        Assert.IsFalse(relay.IsCompleted);
        Assert.IsTrue(origin.Reads <= 2, $"the origin was read {origin.Reads} times while the client was stalled");

        cts.Cancel();
        release.SetResult();
        await AssertCanceledAsync(relay);
    }

    private static IEnumerable<byte[]> Endless(byte[] piece)
    {
        while (true)
        {
            yield return piece;
        }
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("expected the relay to be canceled");
    }

    [TestMethod]
    public async Task Relay_ClientGone_StopsReadingAndWritesNoTrailer()
    {
        var body = Text(200_000);
        var origin = new PieceStream(body.Chunk(10_000));
        var sink = new RecordingSink();
        var gone = false;
        var reads = 0;

        var done = await StreamingResponseRelay.RelayAsync(origin, sink, HttpCompression.Gzip, CompressionLevel.Fastest,
            new byte[16 * 1024], body.Length, (_, _, _) =>
            {
                if (++reads == 3)
                {
                    gone = true;
                }
            }, () => gone, CancellationToken.None);

        Assert.IsFalse(done);
        Assert.AreEqual(3, origin.Reads, "no origin read after the client left");
        var written = sink.Bytes;
        Assert.IsTrue(written.Length > 0);

        // A flushed gzip stream ends in the empty stored block (00 00 FF FF); the final block and the CRC32 + ISIZE
        // trailer are only written on a clean finish.
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0xFF, 0xFF }, written[^4..]);
        Assert.AreEqual(30_000, Decode(HttpCompression.Gzip, written).Length);
    }

    [TestMethod]
    public async Task Relay_ShortOrigin_FailsWhenALengthWasDeclared()
    {
        var origin = new PieceStream([Text(1000)]);
        var sink = new RecordingSink();

        await Assert.ThrowsExceptionAsync<IOException>(() => StreamingResponseRelay.RelayAsync(origin, sink,
            HttpCompression.Gzip, CompressionLevel.Fastest, new byte[8192], 5000, null, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Relay_Cancellation_Propagates_AndSkipsTheTrailer()
    {
        using var cts = new CancellationTokenSource();
        var sink = new RecordingSink(_ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });
        var origin = new PieceStream(Enumerable.Repeat(Text(4000), 100));

        await AssertCanceledAsync(StreamingResponseRelay.RelayAsync(origin, sink, HttpCompression.Brotli,
            CompressionLevel.Fastest, new byte[8192], -1, null, null, cts.Token));

        Assert.IsTrue(origin.Reads < 100);
    }

    [TestMethod]
    public async Task Relay_ReportsEveryOriginPiece()
    {
        var body = Text(50_000);
        var seen = 0;
        var origin = new PieceStream(body.Chunk(5_000));
        await StreamingResponseRelay.RelayAsync(origin, new RecordingSink(), HttpCompression.Gzip,
            CompressionLevel.Fastest, new byte[16 * 1024], -1, (_, _, count) => seen += count, null,
            CancellationToken.None);
        Assert.AreEqual(body.Length, seen);
    }

    // ---- request decompression ------------------------------------------------------------------------

    private static RequestDecompressionPolicy Requests(int maxBytes = 1024 * 1024, int ratio = 100) =>
        RequestDecompressionPolicy.Create(new RequestDecompressionConfig
        {
            Enabled = true,
            MaxDecompressedBytes = maxBytes,
            MaxRatio = ratio,
        })!;

    /// <summary>Text that compresses well but far below the 100:1 ratio limit.</summary>
    private static byte[] Varied(int length)
    {
        var random = new Random(11);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india" };
        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }

        return Encoding.ASCII.GetBytes(text.ToString())[..length];
    }

    private static byte[] Encode(HttpCompression algorithm, byte[] body,
        CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream();
        using (Stream encoder = algorithm == HttpCompression.Brotli
                   ? new BrotliStream(output, level, leaveOpen: true)
                   : new GZipStream(output, level, leaveOpen: true))
        {
            encoder.Write(body, 0, body.Length);
        }

        return output.ToArray();
    }

    [TestMethod]
    public void RequestDecompression_PolicyIsNullUnlessEnabled()
    {
        Assert.IsNull(RequestDecompressionPolicy.Create(null));
        Assert.IsNull(RequestDecompressionPolicy.Create(new RequestDecompressionConfig()));
        Assert.IsNotNull(RequestDecompressionPolicy.Create(new RequestDecompressionConfig { Enabled = true }));
    }

    [TestMethod]
    [DataRow("gzip", true)]
    [DataRow("GZIP", true)]
    [DataRow("x-gzip", true)]
    [DataRow("br", true)]
    [DataRow("deflate", false)]
    [DataRow("gzip, br", false)]
    [DataRow("zstd", false)]
    [DataRow("identity", false)]
    public void RequestDecompression_OnlyGzipAndBrotli(string encoding, bool expected)
    {
        var request = new Request { Method = "POST", HttpVersion = HttpHeader.Version11 };
        request.Headers.AddHeader("Content-Encoding", encoding);
        Assert.AreEqual(expected, RequestDecompressionPolicy.TryGetEncoding(request, out _));
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    public void RequestDecompression_RoundTrips(string name)
    {
        var algorithm = Parse(name);
        var body = Varied(200_000);
        var outcome = Requests().TryDecompress(algorithm, Encode(algorithm, body), out var plain);

        Assert.AreEqual(RequestDecompressionOutcome.Decompressed, outcome);
        CollectionAssert.AreEqual(body, plain);
    }

    [TestMethod]
    public void RequestDecompression_EmptyBodyIsFine()
    {
        var outcome = Requests().TryDecompress(HttpCompression.Gzip, Encode(HttpCompression.Gzip, []), out var plain);
        Assert.AreEqual(RequestDecompressionOutcome.Decompressed, outcome);
        Assert.AreEqual(0, plain.Length);
    }

    [TestMethod]
    public void RequestDecompression_SizeLimit_StopsAtTheCap()
    {
        // Random-ish text that compresses < 100:1, so only the absolute cap can trip.
        var body = new byte[100_000];
        new Random(7).NextBytes(body);
        var outcome = Requests(maxBytes: 64 * 1024).TryDecompress(HttpCompression.Gzip,
            Encode(HttpCompression.Gzip, body), out var plain);

        Assert.AreEqual(RequestDecompressionOutcome.TooLarge, outcome);
        Assert.AreEqual(0, plain.Length, "nothing is handed back for a breach");
    }

    [TestMethod]
    public void RequestDecompression_RatioLimit_CatchesAZipBomb()
    {
        // 20 MiB of zeros compresses to about 20 KiB (~1000:1).
        var bomb = Encode(HttpCompression.Gzip, new byte[20 * 1024 * 1024], CompressionLevel.SmallestSize);
        Assert.IsTrue(bomb.Length < 100_000, $"test setup: bomb is {bomb.Length} bytes");

        var outcome = Requests(maxBytes: 64 * 1024 * 1024, ratio: 100).TryDecompress(HttpCompression.Gzip, bomb,
            out var plain);

        Assert.AreEqual(RequestDecompressionOutcome.RatioExceeded, outcome);
        Assert.AreEqual(0, plain.Length);
    }

    [TestMethod]
    public void RequestDecompression_RatioLimit_StopsEarly_NotAfterFullExpansion()
    {
        // The result must be refused after about ratio x compressed bytes, not after a 64 MiB inflation.
        var bomb = Encode(HttpCompression.Brotli, new byte[64 * 1024 * 1024]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var outcome = Requests(maxBytes: 64 * 1024 * 1024, ratio: 10).TryDecompress(HttpCompression.Brotli, bomb,
            out _);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(RequestDecompressionOutcome.RatioExceeded, outcome);
        Assert.IsTrue(allocated < 8 * 1024 * 1024, $"allocated {allocated:N0} bytes before refusing");
    }

    [TestMethod]
    public void RequestDecompression_RatioFloor_AllowsASmallHighlyCompressibleBody()
    {
        // 4 KiB of zeros compresses to ~30 bytes (>100:1) but is under the floor.
        var outcome = Requests().TryDecompress(HttpCompression.Gzip, Encode(HttpCompression.Gzip, new byte[4000]),
            out var plain);
        Assert.AreEqual(RequestDecompressionOutcome.Decompressed, outcome);
        Assert.AreEqual(4000, plain.Length);
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    public void RequestDecompression_Corrupt(string name)
    {
        var algorithm = Parse(name);
        var bad = Encoding.ASCII.GetBytes("this is not a compressed stream, it is plain text that merely looks long enough");

        var outcome = Requests().TryDecompress(algorithm, bad, out _);
        Assert.AreEqual(RequestDecompressionOutcome.Corrupt, outcome);
    }

    [TestMethod]
    public void RequestDecompression_Truncated()
    {
        var good = Encode(HttpCompression.Gzip, Varied(50_000));
        Assert.AreEqual(RequestDecompressionOutcome.Corrupt,
            Requests().TryDecompress(HttpCompression.Gzip, good[..(good.Length - 20)], out _));
        Assert.AreEqual(RequestDecompressionOutcome.Corrupt,
            Requests().TryDecompress(HttpCompression.Gzip, good[..(good.Length / 2)], out _));
        Assert.AreEqual(RequestDecompressionOutcome.Corrupt,
            Requests().TryDecompress(HttpCompression.Gzip, good[..(good.Length - 1)], out _));
    }

    [TestMethod]
    public void RequestDecompression_TruncatedBrotli()
    {
        var good = Encode(HttpCompression.Brotli, Varied(50_000));
        Assert.AreEqual(RequestDecompressionOutcome.Corrupt,
            Requests().TryDecompress(HttpCompression.Brotli, good[..(good.Length / 2)], out _));
    }

    // ---- server wiring ---------------------------------------------------------------------------------

    [TestMethod]
    public void ProxyServer_RequestDecompression_IsOffByDefault_AndForcesTheSessionPathWhenOn()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsNull(proxy.RequestDecompression);
        Assert.IsNull(proxy.RequestDecompressionPolicy);
        Assert.IsFalse(proxy.NeedsHttpInterception());

        proxy.RequestDecompression = new RequestDecompressionConfig { Enabled = false };
        Assert.IsNull(proxy.RequestDecompressionPolicy);
        Assert.IsFalse(proxy.NeedsHttpInterception());

        proxy.RequestDecompression = new RequestDecompressionConfig { Enabled = true };
        Assert.IsNotNull(proxy.RequestDecompressionPolicy);
        Assert.IsTrue(proxy.NeedsHttpInterception());

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            ForwardCleartext = true,
        };
        var get = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endPoint, get, false, false, false, null));

        proxy.RequestDecompression = null;
        Assert.IsTrue(proxy.CanUseH1TerminateLite(endPoint, get, false, false, false, null));
    }

    [TestMethod]
    public void ProxyServer_StreamingMode_StillRefusesTerminateLite()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ResponseCompression = new ResponseCompressionConfig { Enabled = true, Mode = "streaming" };
        Assert.IsTrue(proxy.CompressionPolicy!.Streaming);
        Assert.IsTrue(proxy.NeedsHttpInterception());

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            ForwardCleartext = true,
        };
        var get = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endPoint, get, false, false, false, null));
    }
}
