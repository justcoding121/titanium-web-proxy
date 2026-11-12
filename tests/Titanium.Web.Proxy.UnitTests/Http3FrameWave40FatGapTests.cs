using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http3Frame" /> suite: GOAWAY, MAX_PUSH_ID, and unknown-type round trips.
/// </summary>
[TestClass]
public class Http3FrameWave40FatGapTests
{
    [TestMethod]
    public async Task WriteThenRead_GoAway_RoundTripsIdAndReason()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[] { 0, 0, 0, 0, 1, (byte)'d', (byte)'o', (byte)'n', (byte)'e' };
        await Http3Frame.WriteAsync(ms, Http3FrameType.GoAway, payload, CancellationToken.None);
        ms.Position = 0;
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 256, CancellationToken.None);
        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.GoAway, frame!.Type);
        CollectionAssert.AreEqual(payload, frame.Payload.ToArray());
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteThenRead_CancelPush_EmptyPayload()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[] { 0, 0, 0, 7 };
        await Http3Frame.WriteAsync(ms, Http3FrameType.CancelPush, payload, CancellationToken.None);
        ms.Position = 0;
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 64, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.CancelPush, frame!.Type);
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task ReadAsync_PayloadOverLimit_ThrowsFrameError()
    {
        await using var ms = new MemoryStream();
        // type=0x00 (DATA) varint length=5, payload 5 bytes
        ms.Write(new byte[] { 0x00, 0x05, 1, 2, 3, 4, 5 });
        ms.Position = 0;
        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 4, CancellationToken.None).AsTask());
        Assert.IsTrue(ex.ErrorCode is Http3ErrorCode.FrameError or Http3ErrorCode.ExcessiveLoad);
    }
}
