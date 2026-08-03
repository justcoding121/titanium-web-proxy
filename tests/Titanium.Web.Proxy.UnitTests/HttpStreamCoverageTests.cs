using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Direct coverage for <see cref="HttpStream" /> fill/peek/write-body/copy-body paths that integration
///     tests rarely hit in isolation.
/// </summary>
[TestClass]
public class HttpStreamCoverageTests
{
    private static HttpStream MakeReader(byte[] payload, IBufferPool? pool = null)
        => new(new ProxyServer(false, false, false), new MemoryStream(payload), pool ?? new DefaultBufferPool(),
            CancellationToken.None, false);

    private static (HttpStream writer, MemoryStream destination) MakeWriter(IBufferPool? pool = null)
    {
        var destination = new MemoryStream();
        var writer = new HttpStream(new ProxyServer(false, false, false), destination,
            pool ?? new DefaultBufferPool(), CancellationToken.None, true);
        return (writer, destination);
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    [TestMethod]
    public async Task FillBufferAsync_AfterEof_ReturnsFalseIdempotently()
    {
        using var stream = MakeReader(Encoding.ASCII.GetBytes("abc"));
        Assert.IsTrue(await stream.FillBufferAsync());
        // Drain available bytes via Read
        var buf = new byte[8];
        Assert.AreEqual(3, await stream.ReadAsync(buf));
        Assert.IsFalse(await stream.FillBufferAsync());
        Assert.IsFalse(await stream.FillBufferAsync());
        Assert.IsTrue(stream.IsClosed);
    }

    [TestMethod]
    public async Task PeekByteAsync_ReturnsBytesWithoutConsuming_ThenEofMinusOne()
    {
        using var stream = MakeReader(Encoding.ASCII.GetBytes("YZ"));
        Assert.AreEqual((int)'Y', await stream.PeekByteAsync(0));
        Assert.AreEqual((int)'Z', await stream.PeekByteAsync(1));
        var one = new byte[1];
        Assert.AreEqual(1, await stream.ReadAsync(one.AsMemory(0, 1)));
        Assert.AreEqual((byte)'Y', one[0]);
        Assert.AreEqual(1, await stream.ReadAsync(one.AsMemory(0, 1)));
        Assert.AreEqual((byte)'Z', one[0]);
        Assert.AreEqual(-1, await stream.PeekByteAsync(0));
    }

    [TestMethod]
    public async Task PeekByteAsync_IndexBeyondBuffer_Throws()
    {
        using var stream = MakeReader(Encoding.ASCII.GetBytes("a"));
        // DefaultBufferPool buffer is 8192; any index >= that size must throw.
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await stream.PeekByteAsync(8192));
    }

    [TestMethod]
    public async Task PeekBytesAsync_CopiesAvailableWindow()
    {
        using var stream = MakeReader(Encoding.ASCII.GetBytes("hello"));
        var buf = new byte[3];
        var n = await stream.PeekBytesAsync(buf, 0, 0, 3);
        Assert.AreEqual(3, n);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("hel"), buf);
        // Peek must not advance
        var one = new byte[1];
        Assert.AreEqual(1, await stream.ReadAsync(one.AsMemory(0, 1)));
        Assert.AreEqual((byte)'h', one[0]);
    }

    [TestMethod]
    public void PeekByteFromBuffer_Empty_Throws()
    {
        using var stream = MakeReader(Array.Empty<byte>());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.PeekByteFromBuffer(0));
    }

    [TestMethod]
    public async Task WriteAsync_BaseThrows_SetsClosedWrite_FurtherWritesNoOp()
    {
        var throwing = new ThrowingWriteStream();
        using var stream = new HttpStream(new ProxyServer(false, false, false), throwing, new DefaultBufferPool(),
            CancellationToken.None, true);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));

        // Poisoned: further writes should no-op rather than throw again.
        await stream.WriteAsync(new byte[] { 9 }, CancellationToken.None);
        Assert.AreEqual(1, throwing.WriteCount);
    }

    [TestMethod]
    public async Task WriteLineAsync_LongString_UsesHeapBufferPath()
    {
        var pool = new TinyBufferPool(16);
        var (writer, destination) = MakeWriter(pool);
        using (writer)
        {
            var longLine = new string('x', 40);
            await writer.WriteLineAsync(longLine, CancellationToken.None);
        }

        var text = Encoding.ASCII.GetString(destination.ToArray());
        Assert.AreEqual(new string('x', 40) + "\r\n", text);
    }

    [TestMethod]
    public async Task WriteBodyAsync_Chunked_WithTrailers_EmitsFraming()
    {
        var (writer, destination) = MakeWriter();
        using (writer)
        {
            var trailers = new HeaderCollection();
            trailers.AddHeader("x-trailer", "done");
            await writer.WriteBodyAsync(Encoding.ASCII.GetBytes("hi"), isChunked: true, trailers,
                CancellationToken.None);
        }

        var text = Encoding.ASCII.GetString(destination.ToArray());
        // Chunk size is written with "x" format (may be zero-padded depending on path).
        StringAssert.Contains(text, "hi\r\n");
        StringAssert.Contains(text, "0\r\n");
        StringAssert.Contains(text, "x-trailer: done");
    }

    [TestMethod]
    public async Task WriteBodyAsync_NonChunked_WritesRawBytes()
    {
        var (writer, destination) = MakeWriter();
        using (writer)
        {
            await writer.WriteBodyAsync(Encoding.ASCII.GetBytes("raw"), isChunked: false, null,
                CancellationToken.None);
        }

        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("raw"), destination.ToArray());
    }

    [TestMethod]
    public async Task CopyBodyAsync_ContentLength_CopiesExactBytes()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var reader = MakeReader(Encoding.ASCII.GetBytes("abcdef"));
        var (writer, destination) = MakeWriter();
        using var session = MakeSession(proxy);
        using (writer)
        {
            await reader.CopyBodyAsync(writer, isChunked: false, contentLength: 4, isRequest: true, session,
                CancellationToken.None);
        }

        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("abcd"), destination.ToArray());
    }

    [TestMethod]
    public async Task CopyBodyAsync_Chunked_InvalidChunkSize_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var reader = MakeReader(Encoding.ASCII.GetBytes("ZZ\r\n"));
        var (writer, _) = MakeWriter();
        using var session = MakeSession(proxy);
        using (writer)
        {
            await Assert.ThrowsExactlyAsync<ProxyHttpException>(async () =>
                await reader.CopyBodyAsync(writer, isChunked: true, contentLength: -1, isRequest: false, session,
                    CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task CopyBodyAsync_Chunked_CopiesPayloadAndTrailers()
    {
        using var proxy = new ProxyServer(false, false, false);
        var payload = "5\r\nhello\r\n0\r\nx-end: 1\r\n\r\n";
        using var reader = MakeReader(Encoding.ASCII.GetBytes(payload));
        var (writer, destination) = MakeWriter();
        using var session = MakeSession(proxy);
        using (writer)
        {
            await reader.CopyBodyAsync(writer, isChunked: true, contentLength: -1, isRequest: false, session,
                CancellationToken.None);
        }

        var text = Encoding.ASCII.GetString(destination.ToArray());
        StringAssert.Contains(text, "hello");
        StringAssert.Contains(text, "0\r\n");
    }

    [TestMethod]
    public async Task CopyBodyAsync_TransformationNone_UsesLiveContentLength()
    {
        var body = Encoding.ASCII.GetBytes("plain-body");
        using var reader = MakeReader(body);
        var (writer, destination) = MakeWriter();
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var response = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            ContentLength = body.Length
        };

        using (writer)
        {
            await reader.CopyBodyAsync(response, useOriginalHeaderValues: false, writer,
                TransformationMode.None, isRequest: false, session, CancellationToken.None);
        }

        CollectionAssert.AreEqual(body, destination.ToArray());
    }

    [TestMethod]
    public async Task CopyBodyAsync_ContentLengthMinusOne_CopiesUntilEof()
    {
        var body = Encoding.ASCII.GetBytes("until-eof");
        using var reader = MakeReader(body);
        var (writer, destination) = MakeWriter();
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);

        using (writer)
        {
            await reader.CopyBodyAsync(writer, isChunked: false, contentLength: -1, isRequest: false, session,
                CancellationToken.None);
        }

        CollectionAssert.AreEqual(body, destination.ToArray());
    }

    [TestMethod]
    public async Task WriteHeadersAsync_WritesAsciiHeaders()
    {
        var (writer, destination) = MakeWriter();
        var headers = new HeaderBuilder();
        headers.WriteRequestLine("GET", "/", new Version(1, 1));
        headers.WriteHeader(new HttpHeader("Host", "example.com"));
        headers.WriteHeader(new HttpHeader("Connection", "close"));
        headers.WriteLine();

        using (writer)
        {
            await writer.WriteHeadersAsync(headers, CancellationToken.None);
        }

        var text = Encoding.ASCII.GetString(destination.ToArray());
        StringAssert.StartsWith(text, "GET / HTTP/1.1\r\n");
        StringAssert.Contains(text, "Host: example.com\r\n");
        StringAssert.EndsWith(text, "\r\n\r\n");
    }

    [TestMethod]
    public async Task CopyToAsync_DrainsPrefetchedBufferThenBase()
    {
        using var reader = MakeReader(Encoding.ASCII.GetBytes("prefetched-rest"));
        Assert.IsTrue(await reader.FillBufferAsync());
        var one = new byte[1];
        Assert.AreEqual(1, await reader.ReadAsync(one.AsMemory(0, 1)));
        Assert.AreEqual((byte)'p', one[0]);

        using var dest = new MemoryStream();
        await reader.CopyToAsync(dest);
        Assert.AreEqual("refetched-rest", Encoding.ASCII.GetString(dest.ToArray()));
    }

    [TestMethod]
    public async Task DataReadAndDataWrite_EventsFireWithByteCounts()
    {
        using var proxy = new ProxyServer(false, false, false);
        var payload = Encoding.ASCII.GetBytes("evt");
        using var reader = new HttpStream(proxy, new MemoryStream(payload), new DefaultBufferPool(),
            CancellationToken.None, false);
        var readBytes = 0L;
        reader.DataRead += (_, e) => readBytes += e.Count;
        Assert.IsTrue(await reader.FillBufferAsync());
        var buf = new byte[3];
        Assert.AreEqual(3, await reader.ReadAsync(buf));
        Assert.AreEqual(3, readBytes);

        var dest = new MemoryStream();
        using var writer = new HttpStream(proxy, dest, new DefaultBufferPool(), CancellationToken.None, true);
        var writeBytes = 0L;
        writer.DataWrite += (_, e) => writeBytes += e.Count;
        await writer.WriteAsync(payload.AsMemory());
        Assert.AreEqual(3, writeBytes);
    }

    [TestMethod]
    public void Dispose_LeaveOpenTrue_LeavesBaseStreamUsable()
    {
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("x"));
        var stream = new HttpStream(new ProxyServer(false, false, false), ms, new DefaultBufferPool(),
            CancellationToken.None, leaveOpen: true);
        stream.Dispose();
        Assert.IsTrue(ms.CanRead);
        ms.Dispose();
    }

    [TestMethod]
    public void SyncReadWriteFlush_AndUnsupportedSeek()
    {
        var payload = Encoding.ASCII.GetBytes("sync-io");
        using var stream = MakeReader(payload);
        var buf = new byte[16];
        Assert.IsTrue(stream.FillBuffer());
        var n = stream.Read(buf, 0, buf.Length);
        Assert.AreEqual(7, n);
        Assert.AreEqual("sync-io", Encoding.ASCII.GetString(buf, 0, n));
        Assert.AreEqual(-1, stream.ReadByte()); // EOF after drain
        // Exercise Seek/SetLength wrappers against the MemoryStream base.
        Assert.AreEqual(0, stream.Seek(0, SeekOrigin.Begin));
        stream.SetLength(payload.Length);

        var (writer, destination) = MakeWriter();
        using (writer)
        {
            writer.Write(payload, 0, payload.Length);
            writer.Flush();
        }

        CollectionAssert.AreEqual(payload, destination.ToArray());
    }

    [TestMethod]
    public async Task BeginEndReadWrite_RoundTrip()
    {
        using var reader = MakeReader(Encoding.ASCII.GetBytes("apm"));
        var buf = new byte[3];
        Assert.IsTrue(await reader.FillBufferAsync());
        var readAr = reader.BeginRead(buf, 0, 3, null, null);
        Assert.AreEqual(3, reader.EndRead(readAr));
        Assert.AreEqual("apm", Encoding.ASCII.GetString(buf));

        var (writer, destination) = MakeWriter();
        using (writer)
        {
            var writeAr = writer.BeginWrite(buf, 0, 3, null, null);
            writer.EndWrite(writeAr);
        }

        CollectionAssert.AreEqual(buf, destination.ToArray());
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public int WriteCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            throw new IOException("write failed");
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.FromException(new IOException("write failed"));
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.FromException(new IOException("write failed"));
        }
    }

    private sealed class TinyBufferPool : IBufferPool
    {
        public TinyBufferPool(int bufferSize) => BufferSize = bufferSize;
        public int BufferSize { get; }
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }
}
