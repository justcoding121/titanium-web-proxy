using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2FrameWriterWatermarkTests
{
    [TestMethod]
    public async Task EnqueueAgainstStalledDrain_RaisesHighWaterPastSoftThreshold()
    {
        await using var stall = new StallStream();
        await using var writer = new Http2FrameWriter(stall);

        // Block the drain inside WriteAsync first. Enqueueing the backlog before that races the
        // drain: it can coalesce a partial batch, stall, and leave HighWater under 64.
        var primer = ArrayPool<byte>.Shared.Rent(32);
        writer.EnqueueRented(primer, 32);
        var writeStarted = stall.WaitForWriteStarted();
        var started = await Task.WhenAny(writeStarted, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(writeStarted, started, "drain did not reach WriteAsync");

        for (var i = 0; i < 80; i++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(32);
            writer.EnqueueRented(rented, 32);
        }

        Assert.IsTrue(writer.HighWaterMark >= 64,
            $"expected high-water ≥ 64, got {writer.HighWaterMark}");
        Assert.IsTrue(writer.HighWaterMark >= writer.PendingFrameCount);

        stall.Release();
        await writer.DisposeAsync();
    }

    private sealed class StallStream : Stream
    {
        private readonly TaskCompletionSource<bool> gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => gate.TrySetResult(true);

        public Task WaitForWriteStarted() => started.Task;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
