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

        // First write blocks the drain on StallStream; subsequent frames pile up in the channel.
        for (var i = 0; i < 80; i++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(32);
            writer.EnqueueRented(rented, 32);
        }

        // Drain coalesces up to 64 frames into one WriteAsync that stalls; pending drops for those
        // frames while HighWater still recorded the backlog peak.
        Assert.IsTrue(SpinWait.SpinUntil(() => writer.HighWaterMark >= 64, TimeSpan.FromSeconds(2)),
            $"expected high-water ≥ 64, got {writer.HighWaterMark}");
        Assert.IsTrue(writer.HighWaterMark >= writer.PendingFrameCount);

        stall.Release();
        await writer.DisposeAsync();
    }

    private sealed class StallStream : Stream
    {
        private readonly TaskCompletionSource<bool> gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => gate.TrySetResult(true);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
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
