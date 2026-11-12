using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http3Frame" /> suite: SETTINGS, HEADERS, DATA, PUSH_PROMISE, and MAX_PUSH_ID round trips.
/// </summary>
[TestClass]
public class Http3FrameWave41FatGapTests
{
    [TestMethod]
    public async Task WriteThenRead_Settings_RoundTripsPayload()
    {
        var settings = new byte[] { 0x01, 0x40, 0x00, 0x00, 0x00, 0x10 };
        await using var ms = new MemoryStream();
        await Http3Frame.WriteAsync(ms, Http3FrameType.Settings, settings, CancellationToken.None);
        ms.Position = 0;
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 64, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.Settings, frame!.Type);
        CollectionAssert.AreEqual(settings, frame.Payload.ToArray());
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteThenRead_HeadersAndData_SequentialRead()
    {
        var hdr = Encoding.ASCII.GetBytes("qpack-block");
        var body = "hello-h3"u8.ToArray();
        await using var ms = new MemoryStream();
        await Http3Frame.WriteAsync(ms, Http3FrameType.Headers, hdr, CancellationToken.None);
        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, body, CancellationToken.None);
        ms.Position = 0;
        var h = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 256, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.Headers, h!.Type);
        h.ReturnPayload();
        var d = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 256, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.Data, d!.Type);
        CollectionAssert.AreEqual(body, d.Payload.ToArray());
        d.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteThenRead_PushPromise_AndMaxPushId()
    {
        var pushId = new byte[] { 0, 0, 0, 9 };
        var maxPush = new byte[] { 0, 0, 0, 100 };
        await using var ms = new MemoryStream();
        await Http3Frame.WriteAsync(ms, Http3FrameType.PushPromise, pushId, CancellationToken.None);
        await Http3Frame.WriteAsync(ms, Http3FrameType.MaxPushId, maxPush, CancellationToken.None);
        ms.Position = 0;
        var pp = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 64, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.PushPromise, pp!.Type);
        pp.ReturnPayload();
        var mp = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 64, CancellationToken.None);
        Assert.AreEqual(Http3FrameType.MaxPushId, mp!.Type);
        mp.ReturnPayload();
    }
}
