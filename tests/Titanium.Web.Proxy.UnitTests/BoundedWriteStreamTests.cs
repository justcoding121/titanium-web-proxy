using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class BoundedWriteStreamTests
{
    [TestMethod]
    public void Write_WithinLimit_PassesThroughToInnerStream()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 10);

        bounded.Write(new byte[] { 1, 2, 3 }, 0, 3);
        bounded.Write(new byte[] { 4, 5, 6, 7 }, 0, 4);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, inner.ToArray());
    }

    [TestMethod]
    public void Write_ExceedingCumulativeLimitAcrossMultipleWrites_Throws()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 5);

        bounded.Write(new byte[] { 1, 2, 3 }, 0, 3);

        // Individually each write is small, but their sum exceeds the limit - this is exactly the
        // per-frame-vs-cumulative gap the hardening plan calls out.
        Assert.ThrowsException<BodySizeLimitExceededException>(
            () => bounded.Write(new byte[] { 4, 5, 6 }, 0, 3));
    }

    [TestMethod]
    public async Task WriteAsync_ByteArrayOverload_ExceedingLimit_ThrowsBeforeWritingToInner()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 4);

        await Assert.ThrowsExceptionAsync<BodySizeLimitExceededException>(
            () => bounded.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, 0, 5, CancellationToken.None));

        // The whole over-limit write must be rejected atomically: none of it should have reached the
        // inner stream, so a caller cannot observe a silently truncated body as if it were complete.
        Assert.AreEqual(0, inner.Length);
    }

    [TestMethod]
    public async Task WriteAsync_ReadOnlyMemoryOverload_ExceedingLimit_Throws()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 4);

        await Assert.ThrowsExceptionAsync<BodySizeLimitExceededException>(
            async () => await bounded.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None));
    }

    [TestMethod]
    public void Write_ZeroLimit_IsUnlimited()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 0);

        var data = new byte[10_000];
        bounded.Write(data, 0, data.Length);

        Assert.AreEqual(10_000, inner.Length);
    }

    [TestMethod]
    public void Write_NegativeLimit_IsUnlimited()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: -1);

        var data = new byte[10_000];
        bounded.Write(data, 0, data.Length);

        Assert.AreEqual(10_000, inner.Length);
    }

    [TestMethod]
    public void Write_ExactlyAtLimit_Succeeds()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 5);

        bounded.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);

        Assert.AreEqual(5, inner.Length);
    }
}
