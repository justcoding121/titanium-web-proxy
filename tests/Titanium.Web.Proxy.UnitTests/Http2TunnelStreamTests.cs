using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2TunnelStreamTests
{
    [TestMethod]
    public async Task ReadAsync_CopiesPendingChunksAndSignalsEof()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var disposed = 0;
        using var stream = new Http2TunnelStream(
            channel.Reader,
            (_, _, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            () => Interlocked.Increment(ref disposed));

        await channel.Writer.WriteAsync(new byte[] { 1, 2, 3, 4 });
        channel.Writer.TryComplete();

        var buffer = new byte[3];
        Assert.AreEqual(3, await stream.ReadAsync(buffer, 0, 3));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, buffer);

        Assert.AreEqual(1, await stream.ReadAsync(buffer, 0, 3));
        Assert.AreEqual(4, buffer[0]);

        Assert.AreEqual(0, await stream.ReadAsync(buffer, 0, 3));
    }

    [TestMethod]
    public async Task WriteAsync_ForwardsPayloadWithoutEndStream()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        ReadOnlyMemory<byte> seen = default;
        var endStream = false;

        using var stream = new Http2TunnelStream(
            channel.Reader,
            (payload, es, _) =>
            {
                seen = payload.ToArray();
                endStream = es;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            () => { });

        await stream.WriteAsync(new byte[] { 9, 8, 7 }, 0, 3);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, seen.ToArray());
        Assert.IsFalse(endStream);
    }

    [TestMethod]
    public async Task CompleteWriteAsync_SendsEmptyEndStream()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var endStream = false;
        var length = -1;

        using var stream = new Http2TunnelStream(
            channel.Reader,
            (payload, es, _) =>
            {
                length = payload.Length;
                endStream = es;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            () => { });

        await stream.CompleteWriteAsync(CancellationToken.None);
        Assert.AreEqual(0, length);
        Assert.IsTrue(endStream);
        Assert.IsFalse(stream.CanWrite);
    }
}
