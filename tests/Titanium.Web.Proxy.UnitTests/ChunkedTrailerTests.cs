using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="ChunkedTrailerHelper" />, the strict, size-bounded reader/writer shared by
///     every chunked-trailer read/write code path (<c>HttpStream.CopyBodyChunkedAsync</c>,
///     <c>HttpStream.HandleBodyWrite</c>, <c>LimitedStream</c>, <c>BodyStreamWriter</c>).
/// </summary>
[TestClass]
public class ChunkedTrailerTests
{
    private static HttpStream MakeReader(string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        return new HttpStream(new ProxyServer(), new MemoryStream(bytes), new DefaultBufferPool(),
            CancellationToken.None, false);
    }

    private static (HttpStream writer, MemoryStream destination) MakeWriter()
    {
        var destination = new MemoryStream();
        var writer = new HttpStream(new ProxyServer(), destination, new DefaultBufferPool(),
            CancellationToken.None, true);
        return (writer, destination);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_NoTrailers_ConsumesOnlyTheBlankLineAndLeavesCollectionEmpty()
    {
        // Terminating blank line with nothing after it - and something following in the stream, to prove
        // we stop exactly at the blank line rather than over-consuming.
        using var reader = MakeReader("\r\nGET / HTTP/1.1\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        Assert.IsFalse(trailers.GetEnumerator().MoveNext());

        var nextLine = await reader.ReadLineAsync();
        Assert.AreEqual("GET / HTTP/1.1", nextLine);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_SingleTrailer_IsParsedIntoCollection()
    {
        using var reader = MakeReader("X-Trailer: trailer-value\r\n\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        Assert.AreEqual("trailer-value", trailers.GetFirstHeader("X-Trailer")?.Value);
    }

    private static readonly string[] expected = new[] { "X-First: one", "X-Second: two", "X-Third: three" };

    [TestMethod]
    public async Task ReadTrailingHeaders_MultipleTrailerLines_AreAllParsedAndRawLinesCapturedInOrder()
    {
        using var reader = MakeReader("X-First: one\r\nX-Second: two\r\nX-Third: three\r\n\r\n");
        var trailers = new HeaderCollection();
        var rawLines = new List<string>();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, rawLines);

        Assert.AreEqual("one", trailers.GetFirstHeader("X-First")?.Value);
        Assert.AreEqual("two", trailers.GetFirstHeader("X-Second")?.Value);
        Assert.AreEqual("three", trailers.GetFirstHeader("X-Third")?.Value);

        CollectionAssert.AreEqual(
            expected, rawLines);
    }

    private static readonly string[] expectedDuplicateTrailerValues = new[] { "one", "two" };

    [TestMethod]
    public async Task ReadTrailingHeaders_DuplicateHeaderName_KeepsBothAsNonUniqueHeader()
    {
        using var reader = MakeReader("X-Trailer: one\r\nX-Trailer: two\r\n\r\n");
        var trailers = new HeaderCollection();

        await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null);

        var values = trailers.GetHeaders("X-Trailer")!.Select(h => h.Value).ToArray();
        CollectionAssert.AreEquivalent(expectedDuplicateTrailerValues, values);
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_MalformedLineWithoutColon_ThrowsProxyHttpException()
    {
        using var reader = MakeReader("this-is-not-a-valid-header-line\r\n\r\n");
        var trailers = new HeaderCollection();

        await Assert.ThrowsExactlyAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_TooManyLines_ThrowsProxyHttpException()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ChunkedTrailerHelper.MaxTrailerHeaderCount + 1; i++)
            sb.Append($"X-{i}: v\r\n");
        sb.Append("\r\n");

        using var reader = MakeReader(sb.ToString());
        var trailers = new HeaderCollection();

        await Assert.ThrowsExactlyAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task ReadTrailingHeaders_OversizedBlock_ThrowsProxyHttpException()
    {
        var hugeValue = new string('a', ChunkedTrailerHelper.MaxTrailerHeaderBlockSize + 1);
        using var reader = MakeReader($"X-Trailer: {hugeValue}\r\n\r\n");
        var trailers = new HeaderCollection();

        await Assert.ThrowsExactlyAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.ReadTrailingHeaders(reader, trailers, null));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_NullCollection_WritesOnlyTheBlankTerminator()
    {
        var (writer, destination) = MakeWriter();

        await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, null);

        Assert.AreEqual("\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_WithHeaders_WritesEachLineThenBlankTerminator()
    {
        var (writer, destination) = MakeWriter();
        var trailers = new HeaderCollection();
        trailers.AddHeader("X-Checksum", "abc123");

        await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, trailers);

        Assert.AreEqual("X-Checksum: abc123\r\n\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteTrailingHeadersAsync_ForbiddenField_ThrowsProxyHttpExceptionAndDoesNotSilentlyDrop()
    {
        var (writer, _) = MakeWriter();
        var trailers = new HeaderCollection();
        trailers.AddHeader(KnownHeaders.ContentLength.String, "5");

        await Assert.ThrowsExactlyAsync<ProxyHttpException>(
            async () => await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, trailers));
    }

    [TestMethod]
    public async Task WriteRawTrailingLinesAsync_PreservesExactLineTextAndOrder()
    {
        var (writer, destination) = MakeWriter();
        // Deliberately non-normalized spacing to prove raw lines are forwarded byte-for-byte rather than
        // re-serialized through a parsed HeaderCollection (which would trim/normalize the value).
        var rawLines = new List<string> { "X-Trailer:   spaced-value  ", "X-Other:v2" };

        await ChunkedTrailerHelper.WriteRawTrailingLinesAsync(writer, rawLines);

        Assert.AreEqual("X-Trailer:   spaced-value  \r\nX-Other:v2\r\n\r\n",
            Encoding.ASCII.GetString(destination.ToArray()));
    }

    [TestMethod]
    public async Task WriteRawTrailingLinesAsync_NullList_WritesOnlyTheBlankTerminator()
    {
        var (writer, destination) = MakeWriter();

        await ChunkedTrailerHelper.WriteRawTrailingLinesAsync(writer, null);

        Assert.AreEqual("\r\n", Encoding.ASCII.GetString(destination.ToArray()));
    }
}
