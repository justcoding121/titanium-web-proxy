using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectViewersAndThrottleTests
{
    [TestMethod]
    public void SseEventParser_ParsesEvents()
    {
        var events = SseEventParser.Parse("event: ping\ndata: hi\n\nid: 1\ndata: a\ndata: b\n\n");
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual("ping", events[0].Event);
        Assert.AreEqual("hi", events[0].Data);
        Assert.AreEqual("message", events[1].Event);
        Assert.AreEqual("1", events[1].Id);
        Assert.AreEqual("a\nb", events[1].Data);
    }

    [TestMethod]
    public void SseEventParser_IgnoresCommentsAndEmpty()
    {
        Assert.AreEqual(0, SseEventParser.Parse(null).Count);
        Assert.AreEqual(0, SseEventParser.Parse("").Count);
        Assert.AreEqual(0, SseEventParser.Parse(": keep-alive\n\n").Count);
        var events = SseEventParser.Parse(": comment\ndata: only\n\n");
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("message", events[0].Event);
        Assert.AreEqual("only", events[0].Data);
    }

    [TestMethod]
    public void ProtobufMessageDecoder_DecodesStringField()
    {
        // field 1, wire type 2 (len-delimited), "hi"
        var payload = new byte[] { 0x0a, 0x02, (byte)'h', (byte)'i' };
        var framed = new byte[5 + payload.Length];
        framed[0] = 0;
        framed[1] = 0;
        framed[2] = 0;
        framed[3] = 0;
        framed[4] = (byte)payload.Length;
        payload.CopyTo(framed.AsSpan(5));
        var json = ProtobufMessageDecoder.DecodeWireFormat(framed);
        StringAssert.Contains(json, "\"field\": 1");
        StringAssert.Contains(json, "hi");
    }

    [TestMethod]
    public void ProtobufMessageDecoder_DecodesVarintField_WithoutGrpcFrame()
    {
        // field 2, wire type 0 (varint), value 150
        var payload = new byte[] { 0x10, 0x96, 0x01 };
        var json = ProtobufMessageDecoder.DecodeWireFormat(payload, stripGrpcFrame: false);
        StringAssert.Contains(json, "\"field\": 2");
        StringAssert.Contains(json, "150");
    }

    [TestMethod]
    public void ProtobufMessageDecoder_DecodesFixed64Fixed32TruncatedAndBinary()
    {
        Assert.AreEqual("", ProtobufMessageDecoder.DecodeWireFormat(null));
        Assert.AreEqual("", ProtobufMessageDecoder.DecodeWireFormat([]));

        // field 1, wire type 1 (64-bit)
        var fixed64 = new byte[] { 0x09, 1, 2, 3, 4, 5, 6, 7, 8 };
        StringAssert.Contains(ProtobufMessageDecoder.DecodeWireFormat(fixed64, stripGrpcFrame: false), "\"wireType\": 1");

        // field 1, wire type 5 (32-bit)
        var fixed32 = new byte[] { 0x0d, 1, 2, 3, 4 };
        StringAssert.Contains(ProtobufMessageDecoder.DecodeWireFormat(fixed32, stripGrpcFrame: false), "\"wireType\": 5");

        // truncated length-delimited
        var truncated = new byte[] { 0x0a, 0x10 };
        _ = ProtobufMessageDecoder.DecodeWireFormat(truncated, stripGrpcFrame: false);

        // length-delimited with control bytes → hex
        var binary = new byte[] { 0x0a, 0x02, 0x00, 0x01 };
        StringAssert.Contains(ProtobufMessageDecoder.DecodeWireFormat(binary, stripGrpcFrame: false), "0001");
    }

    [TestMethod]
    public void NetworkThrottle_DelayFor_SlowProfile()
    {
        var delay = NetworkThrottle.DelayFor(NetworkThrottle.Slow3G, byteCount: 50_000, applyLatency: true);
        Assert.IsTrue(delay.TotalMilliseconds >= 1400, $"expected latency+transfer, got {delay.TotalMilliseconds}ms");
    }

    [TestMethod]
    public void NetworkThrottle_DelayFor_None_IsZero()
    {
        Assert.AreEqual(TimeSpan.Zero, NetworkThrottle.DelayFor(NetworkThrottle.None, 1_000_000, applyLatency: true));
        Assert.IsFalse(NetworkThrottle.None.IsEnabled);
        Assert.IsTrue(NetworkThrottle.LTE.IsEnabled);
        Assert.AreSame(NetworkThrottle.Fast3G, NetworkThrottle.Find("Fast 3G"));
    }

    [TestMethod]
    public void NetworkThrottle_DelayFor_SkipsLatencyWhenRequested()
    {
        var withLatency = NetworkThrottle.DelayFor(NetworkThrottle.Slow3G, byteCount: 0, applyLatency: true);
        var without = NetworkThrottle.DelayFor(NetworkThrottle.Slow3G, byteCount: 0, applyLatency: false);
        Assert.AreEqual(400, withLatency.TotalMilliseconds);
        Assert.AreEqual(TimeSpan.Zero, without);
    }

    [TestMethod]
    public void ProtocolFrameInspectors_ParsesWsTextOpcode()
    {
        // FIN + text, len=2, unmasked "ok"
        var frame = new byte[] { 0x81, 0x02, (byte)'o', (byte)'k' };
        var list = ProtocolFrameInspectors.ParseWebSocketFrames(frame);
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("Text", list[0].Opcode);
        Assert.AreEqual("ok", list[0].PayloadPreview);
    }

    [TestMethod]
    public void InterceptionService_ThrottleProfile_Assignment_EnablesDelayFor()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        Assert.IsNull(interception.ThrottleProfile);

        interception.ThrottleProfile = NetworkThrottle.Find("Fast 3G");
        Assert.IsNotNull(interception.ThrottleProfile);
        Assert.IsTrue(interception.ThrottleProfile!.IsEnabled);
        var delay = NetworkThrottle.DelayFor(interception.ThrottleProfile, byteCount: 1000, applyLatency: true);
        Assert.IsTrue(delay > TimeSpan.Zero);

        interception.ThrottleProfile = NetworkThrottle.Find("None") is { IsEnabled: true } p ? p : null;
        Assert.IsNull(interception.ThrottleProfile);
    }

    [TestMethod]
    public void SessionSnapshot_SseParse_MatchesEventStreamBody()
    {
        var body = "event: update\ndata: {\"n\":1}\n\ndata: trailing\n\n";
        var snap = new SessionSnapshot
        {
            Id = 42,
            Method = "GET",
            Url = "http://sse.test/stream",
            ContentType = "text/event-stream",
            IsServerSentEvents = true,
            ResponseBodyText = body,
            SseEvents = SseEventParser.Parse(body),
        };
        Assert.AreEqual(2, snap.SseEvents!.Count);
        Assert.AreEqual("update", snap.SseEvents[0].Event);
        Assert.AreEqual("{\"n\":1}", snap.SseEvents[0].Data);
        Assert.AreEqual("message", snap.SseEvents[1].Event);
        Assert.AreEqual("trailing", snap.SseEvents[1].Data);
    }
}
