using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class HeaderParserTests
{
    [TestMethod]
    public async Task ReadHeaders_when_DataAvailable_false_uses_continue_async()
    {
        var headers = new HeaderCollection();
        await HeaderParser.ReadHeaders(
            new ScriptedLineReader(dataAvailable: false, "Host: cont.example", "X-A: 1", null),
            headers,
            CancellationToken.None);

        Assert.AreEqual("cont.example", headers.GetFirstHeader(KnownHeaders.Host)!.Value);
        Assert.AreEqual("1", headers.GetFirstHeader("X-A")!.Value);
    }

    [TestMethod]
    public async Task ReadHeaders_when_first_ReadLineAsync_not_sync_completed_uses_hasPending()
    {
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new PendingFirstLineReader(first, "X-Pending: yes", null);
        var headers = new HeaderCollection();

        var vt = HeaderParser.ReadHeaders(reader, headers, CancellationToken.None);
        Assert.IsFalse(vt.IsCompleted);

        first.SetResult("Host: pending.example");
        await vt;

        Assert.AreEqual("pending.example", headers.GetFirstHeader(KnownHeaders.Host)!.Value);
        Assert.AreEqual("yes", headers.GetFirstHeader("X-Pending")!.Value);
    }

    [TestMethod]
    public async Task ReadHeaders_hasPending_empty_line_returns_without_more_reads()
    {
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new PendingFirstLineReader(first);
        var headers = new HeaderCollection();

        var vt = HeaderParser.ReadHeaders(reader, headers, CancellationToken.None);
        first.SetResult("");
        await vt;

        Assert.AreEqual(0, headers.HeaderCount());
        Assert.AreEqual(1, reader.ReadLineCallCount);
    }

    [TestMethod]
    public async Task ReadHeaders_sync_buffered_lines_when_DataAvailable()
    {
        var headers = new HeaderCollection();
        await HeaderParser.ReadHeaders(
            new ScriptedLineReader(dataAvailable: true, "Connection: keep-alive", "X-Z: z", ""),
            headers,
            CancellationToken.None);

        Assert.AreSame(KnownHeaders.ConnectionKeepAlive.String,
            headers.GetFirstHeader(KnownHeaders.Connection)!.Value);
        Assert.AreEqual("z", headers.GetFirstHeader("X-Z")!.Value);
    }

    [TestMethod]
    public async Task TryReadHeadersAsync_over_MemoryStream_parses_until_blank_line()
    {
        var payload = Encoding.ASCII.GetBytes(
            "Host: try.example\r\nX-Custom: hello\r\nConnection: close\r\n\r\n");
        using var stream = CreateHttpStream(payload);
        var headers = new HeaderCollection();

        Assert.IsTrue(await HeaderParser.TryReadHeadersAsync(stream, headers, CancellationToken.None));
        Assert.AreEqual("try.example", headers.GetFirstHeader(KnownHeaders.Host)!.Value);
        Assert.AreEqual("hello", headers.GetFirstHeader("X-Custom")!.Value);
        Assert.AreEqual("close", headers.GetFirstHeader(KnownHeaders.Connection)!.Value);
    }

    [TestMethod]
    public async Task TryReadHeadersAsync_byte_path_trims_ascii_spaces_and_tabs()
    {
        // Trailing/leading spaces and tabs around name and value; blank line ends the block.
        var payload = Encoding.ASCII.GetBytes(
            "Host: example.com  \t\r\n" +
            "X-Custom\t :  hello world  \t\r\n" +
            "Connection: keep-alive\t \r\n" +
            "\r\n");

        using var stream = CreateHttpStream(payload);
        Assert.IsTrue(await stream.FillBufferAsync(CancellationToken.None));

        var headers = new HeaderCollection();
        Assert.IsTrue(await HeaderParser.TryReadHeadersAsync(stream, headers, CancellationToken.None));

        Assert.AreEqual("example.com", headers.GetFirstHeader(KnownHeaders.Host)!.Value);
        Assert.AreEqual("hello world", headers.GetFirstHeader("X-Custom")!.Value);
        Assert.AreSame(KnownHeaders.ConnectionKeepAlive.String,
            headers.GetFirstHeader(KnownHeaders.Connection)!.Value);
    }

    [TestMethod]
    public async Task TryReadHeadersAsync_continue_path_when_buffer_empty()
    {
        // No pre-fill: TryConsumeHeaderLineFromBuffer fails immediately → continue/async path.
        var payload = Encoding.ASCII.GetBytes("X-From-Stream: value\r\n\r\n");
        using var stream = CreateHttpStream(payload);
        var headers = new HeaderCollection();

        Assert.IsTrue(await HeaderParser.TryReadHeadersAsync(stream, headers, CancellationToken.None));
        Assert.AreEqual("value", headers.GetFirstHeader("X-From-Stream")!.Value);
    }

    [TestMethod]
    public async Task TryReadHeadersAsync_continue_after_incomplete_line_across_fills()
    {
        // Tiny buffer so the first fill stops mid-line and forces the string continue loop.
        var line = "X-Long: " + new string('a', 40) + "\r\n\r\n";
        var payload = Encoding.ASCII.GetBytes(line);
        var proxy = new ProxyServer(false, false, false);
        using var stream = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(16),
            CancellationToken.None, false);

        var headers = new HeaderCollection();
        Assert.IsTrue(await HeaderParser.TryReadHeadersAsync(stream, headers, CancellationToken.None));

        var header = headers.GetFirstHeader("X-Long");
        Assert.IsNotNull(header);
        Assert.AreEqual(new string('a', 40), header.Value);
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

    /// <summary>ILineStream that returns scripted lines; DataAvailable is fixed or index-based.</summary>
    private sealed class ScriptedLineReader : ILineStream
    {
        private readonly bool? fixedDataAvailable;
        private readonly string?[] lines;
        private int index;

        public ScriptedLineReader(bool dataAvailable, params string?[] lines)
        {
            fixedDataAvailable = dataAvailable;
            this.lines = lines;
        }

        public bool DataAvailable =>
            fixedDataAvailable ?? index < lines.Length;

        public ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken) => new(false);

        public byte ReadByteFromBuffer() => throw new InvalidOperationException();

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (index >= lines.Length)
                return new ValueTask<string?>((string?)null);
            return new ValueTask<string?>(lines[index++]);
        }
    }

    /// <summary>
    ///     First ReadLineAsync returns an incomplete ValueTask (hasPending path); later lines are sync.
    /// </summary>
    private sealed class PendingFirstLineReader : ILineStream
    {
        private readonly TaskCompletionSource<string?> first;
        private readonly string?[] rest;
        private int restIndex;
        private bool firstIssued;

        public PendingFirstLineReader(TaskCompletionSource<string?> first, params string?[] rest)
        {
            this.first = first;
            this.rest = rest;
        }

        public int ReadLineCallCount { get; private set; }

        // Enter the sync while once so ReadHeaders observes the incomplete ValueTask.
        public bool DataAvailable => !firstIssued;

        public ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken) => new(false);

        public byte ReadByteFromBuffer() => throw new InvalidOperationException();

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ReadLineCallCount++;
            if (!firstIssued)
            {
                firstIssued = true;
                return new ValueTask<string?>(first.Task);
            }

            if (restIndex >= rest.Length)
                return new ValueTask<string?>((string?)null);
            return new ValueTask<string?>(rest[restIndex++]);
        }
    }
}
