using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="QpackDecoderStreamWriter" /> draining the decoder ack channel.
/// </summary>
[TestClass]
public class QpackDecoderStreamWriterTests
{
    [TestMethod]
    public async Task RunAsync_WritesEnqueuedSectionAckThenCompletes()
    {
        await using var ctx = new QpackContext(4096);
        await using var ms = new MemoryStream();

        ctx.EnqueueSectionAck(streamId: 4);
        ctx.DecoderAckChannel.Writer.TryComplete();

        await QpackDecoderStreamWriter.RunAsync(ms, ctx, CancellationToken.None);

        Assert.IsTrue(ms.Length > 0, "Expected at least one decoder-stream instruction byte.");
        // Section Ack for stream 4 fits in one byte: 0x80 | 4 = 0x84
        Assert.AreEqual(0x84, ms.ToArray()[0]);
    }

    [TestMethod]
    public async Task RunAsync_Cancelled_ExitsWithoutThrowing()
    {
        await using var ctx = new QpackContext(4096);
        await using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await QpackDecoderStreamWriter.RunAsync(ms, ctx, cts.Token);
        Assert.AreEqual(0, ms.Length, "Cancelled run must not write decoder-stream bytes.");
    }
}
