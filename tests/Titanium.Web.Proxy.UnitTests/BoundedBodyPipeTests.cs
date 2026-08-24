using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class BoundedBodyPipeTests
{
    [TestMethod]
    public void Properties_ExposeReaderWriterAndTotalWritten()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 64);

        Assert.IsNotNull(pipe.Reader);
        Assert.IsNotNull(pipe.Writer);
        Assert.AreEqual(0, pipe.TotalWritten);
    }

    [TestMethod]
    public async Task WriteAsyncSlow_ViaReflection_CoversAwaitAndCanceledFlush()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        var slow = typeof(BoundedBodyPipe).GetMethod("WriteAsyncSlow",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var ok = (ValueTask)slow.Invoke(pipe,
            [new ValueTask<System.IO.Pipelines.FlushResult>(new System.IO.Pipelines.FlushResult(false, false)), 4,
                CancellationToken.None])!;
        await ok;
        Assert.AreEqual(4, pipe.TotalWritten);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            var canceled = (ValueTask)slow.Invoke(pipe,
                [new ValueTask<System.IO.Pipelines.FlushResult>(new System.IO.Pipelines.FlushResult(true, false)), 2,
                    CancellationToken.None])!;
            await canceled;
        });
    }

    [TestMethod]
    public async Task WriteAsync_WhenPipeBackpressures_UsesSlowPath()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        var chunk = new byte[32 * 1024];

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 64; i++)
                await pipe.WriteAsync(chunk);
            pipe.CompleteWriter();
        });

        // Give the producer time to fill the pipe past the pause threshold so later writes await.
        await Task.Delay(50);

        var total = 0L;
        while (true)
        {
            var result = await pipe.Reader.ReadAsync();
            if (result.Buffer.Length > 0)
            {
                total += result.Buffer.Length;
                pipe.Reader.AdvanceTo(result.Buffer.End);
            }

            if (result.IsCompleted)
                break;
        }

        await producer;
        Assert.IsTrue(total >= chunk.Length);
        Assert.IsTrue(pipe.TotalWritten >= chunk.Length);
    }

    [TestMethod]
    public async Task WriteAsync_Unlimited_SucceedsAndTracksTotal()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await pipe.WriteAsync(data);
        pipe.CompleteWriter();

        Assert.AreEqual(5, pipe.TotalWritten);
    }

    [TestMethod]
    public async Task WriteAsync_WithinBound_Succeeds()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 10);
        var data = new byte[] { 1, 2, 3 };

        await pipe.WriteAsync(data);
        await pipe.WriteAsync(new byte[] { 4, 5 });

        Assert.AreEqual(5, pipe.TotalWritten);
    }

    [TestMethod]
    public async Task WriteAsync_ExceedingMaxBytes_ThrowsBodySizeLimitExceeded()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 4);

        await Assert.ThrowsExactlyAsync<BodySizeLimitExceededException>(
            async () => await pipe.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [TestMethod]
    public async Task WriteAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await pipe.WriteAsync(new byte[] { 1 }));
    }

    [TestMethod]
    public async Task WriteAsync_WithCanceledToken_ThrowsOperationCanceled()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await pipe.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token));
    }

    [TestMethod]
    public async Task CompleteWriter_AllowsReaderToDrainAndComplete()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        await pipe.WriteAsync(new byte[] { 9, 8, 7 });
        pipe.CompleteWriter();

        using var ms = new MemoryStream();
        await pipe.CopyToAsync(ms);

        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, ms.ToArray());
    }

    [TestMethod]
    public async Task CompleteReader_ThenWrite_CompletesWithoutThrowingOnUnlimitedPipe()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.CompleteReader();

        // Completing the reader first should not throw on the write itself for an unlimited pipe;
        // the flush may report completed. Just ensure the call returns.
        await pipe.WriteAsync(new byte[] { 1 });
        Assert.AreEqual(1, pipe.TotalWritten);
        pipe.CompleteWriter();
    }

    [TestMethod]
    public async Task CopyToAsync_CopiesAllBytesToMemoryStream()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;

        await pipe.WriteAsync(payload);
        pipe.CompleteWriter();

        using var destination = new MemoryStream();
        await pipe.CopyToAsync(destination);

        CollectionAssert.AreEqual(payload, destination.ToArray());
    }

    [TestMethod]
    public async Task ReadExactAsync_ExactFill_ReturnsFullCount()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        await pipe.WriteAsync(new byte[] { 10, 20, 30, 40 });
        pipe.CompleteWriter();

        var destination = new byte[4];
        var read = await pipe.ReadExactAsync(destination);

        Assert.AreEqual(4, read);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40 }, destination);
    }

    [TestMethod]
    public async Task ReadExactAsync_EarlyWriterComplete_ReturnsPartial()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        await pipe.WriteAsync(new byte[] { 1, 2 });
        pipe.CompleteWriter();

        var destination = new byte[8];
        var read = await pipe.ReadExactAsync(destination);

        Assert.AreEqual(2, read);
        Assert.AreEqual(1, destination[0]);
        Assert.AreEqual(2, destination[1]);
    }

    [TestMethod]
    public async Task ReadExactAsync_EmptyDestination_ReturnsZero()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.CompleteWriter();

        var read = await pipe.ReadExactAsync(Memory<byte>.Empty);
        Assert.AreEqual(0, read);
    }

    [TestMethod]
    public async Task ReadExactAsync_EmptyBufferWhenWriterCompletedWithoutData_ReturnsZero()
    {
        using var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.CompleteWriter();

        var destination = new byte[4];
        var read = await pipe.ReadExactAsync(destination);

        Assert.AreEqual(0, read);
    }

    [TestMethod]
    public async Task ReadExactAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await pipe.ReadExactAsync(new byte[4]));
    }

    [TestMethod]
    public async Task Dispose_Twice_IsIdempotent()
    {
        var pipe = new BoundedBodyPipe(maxBytes: 0);
        pipe.Dispose();
        pipe.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await pipe.WriteAsync(new byte[] { 1 }));
    }
}
