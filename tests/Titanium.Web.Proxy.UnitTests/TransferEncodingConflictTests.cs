using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Characterization / regression for issue #547: chunk framing and CL+TE conflict handling
///     per RFC 9112 §6.3.
/// </summary>
[TestClass]
public class TransferEncodingConflictTests
{
    [TestMethod]
    public void FixProxyHeaders_StripsContentLength_WhenTransferEncodingAlsoPresent()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ContentLength, "5");
        headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);

        headers.NormalizeMessageFraming();

        Assert.IsFalse(headers.HeaderExists(KnownHeaders.ContentLength.String),
            "Content-Length must be removed when Transfer-Encoding is present (RFC 9112 §6.3)");
        Assert.IsTrue(headers.HeaderExists(KnownHeaders.TransferEncoding.String));
    }

    [TestMethod]
    public void FixProxyHeaders_LeavesContentLength_WhenNoTransferEncoding()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ContentLength, "5");

        headers.FixProxyHeaders();

        Assert.AreEqual("5", headers.GetHeaderValueOrNull(KnownHeaders.ContentLength));
    }

    [TestMethod]
    public void LimitedStream_ChunkExtension_IsIgnoredWhenParsingSize()
    {
        // "5;name=value\r\nhello\r\n0\r\n\r\n" — extension after ';' must not break size parse.
        var payload = Encoding.ASCII.GetBytes("5;name=value\r\nhello\r\n0\r\n\r\n");
        using var httpStream = new HttpStream(new ProxyServer(), new MemoryStream(payload),
            new DefaultBufferPool(), CancellationToken.None, false);
        using var limited = new LimitedStream(httpStream, new DefaultBufferPool(), true, -1);

        var buffer = new byte[16];
        var read = limited.Read(buffer, 0, buffer.Length);
        Assert.AreEqual(5, read);
        Assert.AreEqual("hello", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [TestMethod]
    public void LimitedStream_OverflowChunkSize_ThrowsProxyHttpException()
    {
        // Hex larger than int.MaxValue cannot be parsed by int.TryParse → ProxyHttpException.
        var payload = Encoding.ASCII.GetBytes("100000000\r\n");
        using var httpStream = new HttpStream(new ProxyServer(), new MemoryStream(payload),
            new DefaultBufferPool(), CancellationToken.None, false);
        using var limited = new LimitedStream(httpStream, new DefaultBufferPool(), true, -1);

        var buffer = new byte[16];
        Assert.ThrowsException<ProxyHttpException>(() => limited.Read(buffer, 0, buffer.Length));
    }

    [TestMethod]
    public async Task LimitedStream_MultipleTrailers_AreParsed()
    {
        var payload = Encoding.ASCII.GetBytes(
            "3\r\nbye\r\n0\r\nX-One: 1\r\nX-Two: 2\r\n\r\n");
        using var httpStream = new HttpStream(new ProxyServer(), new MemoryStream(payload),
            new DefaultBufferPool(), CancellationToken.None, false);
        var trailers = new HeaderCollection();
        using var limited = new LimitedStream(httpStream, new DefaultBufferPool(), true, -1, trailers);

        var buffer = new byte[16];
        var total = 0;
        int n;
        while ((n = await limited.ReadAsync(buffer, 0, buffer.Length)) > 0)
            total += n;

        Assert.AreEqual(3, total);
        Assert.AreEqual("1", trailers.GetFirstHeader("X-One")?.Value);
        Assert.AreEqual("2", trailers.GetFirstHeader("X-Two")?.Value);
    }
}
