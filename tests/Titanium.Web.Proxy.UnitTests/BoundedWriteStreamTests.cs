using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Options;

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
        Assert.ThrowsExactly<BodySizeLimitExceededException>(
            () => bounded.Write(new byte[] { 4, 5, 6 }, 0, 3));
    }

    [TestMethod]
    public async Task WriteAsync_ByteArrayOverload_ExceedingLimit_ThrowsBeforeWritingToInner()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 4);

        await Assert.ThrowsExactlyAsync<BodySizeLimitExceededException>(
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

        await Assert.ThrowsExactlyAsync<BodySizeLimitExceededException>(
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

    [TestMethod]
    public void Write_ExceedingLimit_UnderObserveMode_DoesNotThrowAndStillWritesToInner()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 3, mode: PolicyMode.Observe);

        // Under Observe, a breach is recorded (not asserted here - see ProxyMetrics) but the write
        // itself must still complete rather than throwing, unlike the Enforce-mode default.
        bounded.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);

        Assert.AreEqual(5, inner.Length);
    }

    [TestMethod]
    public void Write_ExceedingLimit_UnderDisabledMode_NeverConsultsLimit()
    {
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 1, mode: PolicyMode.Disabled);

        var data = new byte[10_000];
        bounded.Write(data, 0, data.Length);

        Assert.AreEqual(10_000, inner.Length);
    }

    [TestMethod]
    public void Write_ExceedingLimit_UnderObserveMode_RecordsBreachOnlyOnce()
    {
        // Regression guard for the `breachRecorded` latch: repeatedly writing past the limit under
        // Observe must not throw on any subsequent write either, since only Enforce ever throws.
        var inner = new MemoryStream();
        var bounded = new BoundedWriteStream(inner, maxBytes: 2, mode: PolicyMode.Observe);

        bounded.Write(new byte[] { 1, 2, 3 }, 0, 3);
        bounded.Write(new byte[] { 4, 5, 6 }, 0, 3);
        bounded.Write(new byte[] { 7, 8, 9 }, 0, 3);

        Assert.AreEqual(9, inner.Length);
    }

    [TestMethod]
    public void StreamCapabilities_MatchWriteOnlyContract()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, maxBytes: 10);

        Assert.IsFalse(bounded.CanRead);
        Assert.IsFalse(bounded.CanSeek);
        Assert.IsTrue(bounded.CanWrite);
    }

    [TestMethod]
    public void LengthAndPosition_DelegateToInnerStream()
    {
        using var inner = new MemoryStream();
        inner.Write(new byte[] { 1, 2, 3 }, 0, 3);
        using var bounded = new BoundedWriteStream(inner, maxBytes: 10);

        Assert.AreEqual(3, bounded.Length);
        Assert.AreEqual(3, bounded.Position);
    }

    [TestMethod]
    public void Position_Set_ThrowsNotSupported()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, maxBytes: 10);

        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Position = 0);
    }

    [TestMethod]
    public async Task Flush_AndFlushAsync_ForwardToInner()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, maxBytes: 10);

        bounded.Write(new byte[] { 1 }, 0, 1);
        bounded.Flush();
        await bounded.FlushAsync(CancellationToken.None);

        Assert.AreEqual(1, inner.Length);
    }

    [TestMethod]
    public void ReadSeekAndSetLength_ThrowNotSupported()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, maxBytes: 10);
        var buffer = new byte[4];

        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Read(buffer, 0, buffer.Length));
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.SetLength(0));
    }

    [TestMethod]
    public async Task WriteAsync_ReadOnlyMemory_WithinLimit_Succeeds()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, maxBytes: 8);
        var payload = new byte[] { 9, 8, 7, 6 };

        await bounded.WriteAsync((ReadOnlyMemory<byte>)payload, CancellationToken.None);

        CollectionAssert.AreEqual(payload, inner.ToArray());
    }
}
