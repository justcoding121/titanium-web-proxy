using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="QpackContext" /> — particularly
///     <see cref="QpackContext.AwaitInsertCountAsync" /> and <see cref="QpackContext.NotifyInsert" />.
/// </summary>
[TestClass]
public class QpackContextTests
{
    [TestMethod]
    public async Task AwaitInsertCountAsync_WhenCountAlreadyMet_ReturnsImmediately()
    {
        await using var ctx = new QpackContext(4096);
        ctx.InboundDecoderTable.Insert("a", "1");
        ctx.NotifyInsert();

        using var cts = new CancellationTokenSource(500);
        // InsertCount is already 1; require 1 — should return without waiting.
        await ctx.AwaitInsertCountAsync(1, cts.Token);
    }

    [TestMethod]
    public async Task AwaitInsertCountAsync_WhenCountNotYetMet_WaitsForNotify()
    {
        await using var ctx = new QpackContext(4096);
        // InsertCount = 0; require 1 — will block until NotifyInsert() is called.

        var waitTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(2000);
            await ctx.AwaitInsertCountAsync(1, cts.Token);
        });

        // Simulate a short delay then insert and notify.
        await Task.Delay(50);
        Assert.IsFalse(waitTask.IsCompleted, "Task should still be waiting.");

        ctx.InboundDecoderTable.Insert("a", "1");
        ctx.NotifyInsert();

        await waitTask; // should complete promptly after notify
    }

    [TestMethod]
    public async Task AwaitInsertCountAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        await using var ctx = new QpackContext(4096);
        // InsertCount = 0; require 5 — will never be met.

        using var cts = new CancellationTokenSource(100); // 100 ms timeout

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await ctx.AwaitInsertCountAsync(5, cts.Token);
        });
    }

    [TestMethod]
    public async Task AwaitInsertCountAsync_AlreadyCancelledToken_ThrowsImmediately()
    {
        await using var ctx = new QpackContext(4096);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await ctx.AwaitInsertCountAsync(1, cts.Token);
        });
    }

    [TestMethod]
    public async Task DisposeAsync_CompletesDecoderAckChannelWriter()
    {
        var ctx = new QpackContext(4096);
        ctx.EnqueueSectionAck(42L);

        await ctx.DisposeAsync();

        // After DisposeAsync, the channel writer should be completed and ReadAllAsync
        // should finish without hanging.
        var reader = ctx.DecoderAckChannel.Reader;
        await foreach (var _ in reader.ReadAllAsync())
        {
            // drain remaining items
        }
    }

    [TestMethod]
    public void EnqueueSectionAck_WhenChannelAtCapacity_DoesNotThrow()
    {
        var ctx = new QpackContext(4096); // no disposal needed in fire-and-forget test
        // Enqueue 1100 acks (more than the bounded capacity of 1000).
        for (int i = 0; i < 1100; i++)
            ctx.EnqueueSectionAck(i);

        Assert.IsTrue(ctx.DecoderAckChannel.Reader.TryRead(out _),
            "The channel should retain acknowledgments while dropping overflow writes.");
    }

    [TestMethod]
    public void DisableOutboundTable_SetsFlag()
    {
        var ctx = new QpackContext(4096); // synchronous test; no async disposal path needed
        Assert.IsFalse(ctx.OutboundTableDisabled);

        ctx.DisableOutboundTable();

        Assert.IsTrue(ctx.OutboundTableDisabled);
    }

    [TestMethod]
    public void EnqueueSectionAck_ProducesValidSectionAckInstruction()
    {
        var ctx = new QpackContext(4096);
        ctx.EnqueueSectionAck(0L);

        Assert.IsTrue(ctx.DecoderAckChannel.Reader.TryRead(out var instruction));
        // Section Acknowledgment: bit pattern 1 xxxxxxx where xxxxxxx = stream ID
        // For stream ID 0: 0x80
        Assert.AreEqual(1, instruction.Length);
        Assert.AreEqual(0x80, instruction[0] & 0x80, "High bit must be set (Section Ack pattern).");
    }
}
