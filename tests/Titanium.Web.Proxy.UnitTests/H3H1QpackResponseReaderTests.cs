using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class H3H1QpackResponseReaderTests
{
    [TestMethod]
    public async Task TryReadAsync_ParsesContentLengthAndRegularHeaders()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Content-Type: text/plain\r\nContent-Length: 5\r\nX-Custom: hi\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(5, result.Value.ContentLength);
        Assert.IsFalse(result.Value.IsChunked);
        Assert.IsFalse(result.Value.ConnectionClose);
        Assert.IsTrue(result.Value.QpackHeaders.Length > 0);
    }

    [TestMethod]
    public async Task TryReadAsync_MarksChunkedAndOmitsTransferEncodingFromQpack()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Transfer-Encoding: chunked\r\nX-A: 1\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.IsChunked);
        Assert.AreEqual(-1, result.Value.ContentLength);
    }

    [TestMethod]
    public async Task TryReadAsync_TransferEncodingWithoutChunked_StillOmitsHeader()
    {
        var payload = Encoding.ASCII.GetBytes("Transfer-Encoding: gzip\r\nContent-Length: 1\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Value.IsChunked);
        Assert.AreEqual(1, result.Value.ContentLength);
    }

    [TestMethod]
    public async Task TryReadAsync_ConnectionClose_SetsFlagAndOmitsHopByHop()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Connection: close\r\nKeep-Alive: timeout=5\r\nProxy-Connection: close\r\nUpgrade: h2c\r\nX-Ok: 1\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.ConnectionClose);
    }

    [TestMethod]
    public async Task TryReadAsync_ConnectionKeepAlive_DoesNotSetCloseFlag()
    {
        var payload = Encoding.ASCII.GetBytes("Connection: keep-alive\r\nContent-Length: 0\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Value.ConnectionClose);
    }

    [TestMethod]
    public async Task TryReadAsync_TrimsAsciiWhitespaceOnBytePath()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Content-Length\t :  12  \r\nX-Custom :  value  \t\r\n\r\n");
        using var stream = CreateHttpStream(payload);
        Assert.IsTrue(await stream.FillBufferAsync(CancellationToken.None));

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 204, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(12, result.Value.ContentLength);
    }

    [TestMethod]
    public async Task TryReadAsync_MalformedHeader_ThrowsFormatException()
    {
        var payload = Encoding.ASCII.GetBytes("NotAHeaderWithoutColon\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        await Assert.ThrowsExactlyAsync<FormatException>(async () =>
            await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task TryReadAsync_ContinuePath_WhenBufferInitiallyEmpty()
    {
        var payload = Encoding.ASCII.GetBytes("X-From-Stream: yes\r\nContent-Length: 0\r\n\r\n");
        using var stream = CreateHttpStream(payload);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Value.ContentLength);
    }

    [TestMethod]
    public async Task TryReadAsync_ContinueAfterIncompleteLine_AcrossFills()
    {
        var line = "X-Long: " + new string('a', 40) + "\r\nDate: Wed, 01 Jan 2020 00:00:00 GMT\r\nServer: unit\r\nHost: h\r\n\r\n";
        var payload = Encoding.ASCII.GetBytes(line);
        var proxy = new ProxyServer(false, false, false);
        using var stream = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(16),
            CancellationToken.None, false);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.QpackHeaders.Length > 0);
    }

    [TestMethod]
    public async Task TryReadAsync_KnownHeadersAndMixedCaseCustom_EncodeToQpack()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Content-Type: application/json\r\n" +
            "Content-Length: 0\r\n" +
            "Date: Tue, 15 Nov 1994 08:12:31 GMT\r\n" +
            "Server: Example\r\n" +
            "CONNECTION: Close\r\n" +
            "X-Mixed-CASE: Value\r\n" +
            "\r\n");
        using var stream = CreateHttpStream(payload);
        Assert.IsTrue(await stream.FillBufferAsync(CancellationToken.None));

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 502, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.ConnectionClose);
        Assert.AreEqual(0, result.Value.ContentLength);
    }

    [TestMethod]
    public async Task TryReadAsync_BytePath_ChunkedAndCloseCaseInsensitive()
    {
        var payload = Encoding.ASCII.GetBytes(
            "transfer-encoding: CHUNKED\r\nconnection: CLOSE\r\n\r\n");
        using var stream = CreateHttpStream(payload);
        Assert.IsTrue(await stream.FillBufferAsync(CancellationToken.None));

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.IsChunked);
        Assert.IsTrue(result.Value.ConnectionClose);
    }

    [TestMethod]
    public async Task TryReadAsync_StringContinuePath_MalformedThrows()
    {
        // Tiny buffer forces ReadLineWithResultAsync for the malformed line.
        var payload = Encoding.ASCII.GetBytes("BadHeader\r\n\r\n");
        var proxy = new ProxyServer(false, false, false);
        using var stream = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(4),
            CancellationToken.None, false);

        await Assert.ThrowsExactlyAsync<FormatException>(async () =>
            await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task TryReadAsync_StringContinuePath_ChunkedAndHopByHop()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Transfer-Encoding: chunked\r\nKeep-Alive: timeout=1\r\nProxy-Connection: keep-alive\r\nUpgrade: websocket\r\nX-Z: 1\r\n\r\n");
        var proxy = new ProxyServer(false, false, false);
        using var stream = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(8),
            CancellationToken.None, false);

        var result = await H3H1QpackResponseReader.TryReadAsync(stream, 200, null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.IsChunked);
    }

    private static HttpStream CreateHttpStream(byte[] payload) =>
        new(new ProxyServer(false, false, false), new MemoryStream(payload), new DefaultBufferPool(),
            CancellationToken.None, false);

    private sealed class FixedSizeBufferPool : IBufferPool
    {
        public FixedSizeBufferPool(int bufferSize) => BufferSize = bufferSize;
        public int BufferSize { get; }
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }
}
