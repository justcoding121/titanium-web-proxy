using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectViewersAndThrottleTests
{
    [TestMethod]
    public void Settings_WithRemovedProgressiveThrottleDeliveryKey_StillLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"networkThrottleProfile\": \"Fast 3G\", \"progressiveThrottleDelivery\": false }");

            var settings = new SettingsService(path);

            Assert.AreEqual("Fast 3G", settings.Current.NetworkThrottleProfile);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
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

    [TestMethod]
    public void Hooks_WithProfileNone_DoNotAllocate()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = null,
        };
        using var session = MakeSession();
        session.HttpClient.Request.IsBodyRead = true;
        session.HttpClient.Response.IsBodyRead = true;
        var args = new BeforeBodyWriteEventArgs(session, [1, 2, 3], isChunked: true, isLastChunk: false);
        interception.OnRequestBodyWriteThrottle(interception, args);
        interception.OnResponseBodyWriteThrottle(interception, args);
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            interception.OnRequestBodyWriteThrottle(interception, args);
            interception.OnResponseBodyWriteThrottle(interception, args);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated);
    }

    [TestMethod]
    public void Http2Relay_DoesNotSleepInTheBodyWriteHook()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = NetworkThrottle.Slow3G,
        };
        using var session = MakeSession();
        session.HttpClient.Request.HttpVersion = new Version(2, 0);
        session.HttpClient.Response.HttpVersion = new Version(2, 0);
        session.HttpClient.Request.IsBodyRead = true;
        session.HttpClient.Response.IsBodyRead = true;
        var args = new BeforeBodyWriteEventArgs(session, new byte[8 * 1024], isChunked: true, isLastChunk: false);
        var started = DateTime.UtcNow;
        interception.OnRequestBodyWriteThrottle(interception, args).GetAwaiter().GetResult();
        interception.OnResponseBodyWriteThrottle(interception, args).GetAwaiter().GetResult();
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(200));
    }

    [TestMethod]
    public void Http1Relay_DoesNotAddLatencyOnEveryChunk()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = new NetworkThrottleProfile("lat", latencyMs: 500, bytesPerSecond: int.MaxValue),
        };
        using var session = MakeSession();
        session.HttpClient.Request.HttpVersion = new Version(1, 1);
        session.HttpClient.Response.HttpVersion = new Version(1, 1);
        var args = new BeforeBodyWriteEventArgs(session, new byte[128], isChunked: true, isLastChunk: false);
        var started = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            interception.OnResponseBodyWriteThrottle(interception, args).GetAwaiter().GetResult();
        }

        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(400));
    }

    [TestMethod]
    public void ProfileSwap_ReplacesTheShaper()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = NetworkThrottle.Slow3G,
        };
        interception.ThrottleProfile = NetworkThrottle.LTE;
        interception.ThrottleProfile = null;
        Assert.IsNull(interception.ThrottleProfile);
        interception.ThrottleProfile = NetworkThrottle.Fast3G;
        Assert.AreEqual("Fast 3G", interception.ThrottleProfile.Name);
    }

    [TestMethod]
    public async Task Http2Response_NeverSleepsInBeforeResponse_AndIsStreamedInstead()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = NetworkThrottle.Slow3G,
        };
        using var session = MakeHttp2Session(bodyLength: 100 * 1024);
        var original = session.HttpClient.Response;

        var started = DateTime.UtcNow;
        await interception.ApplyResponseThrottleAsync(session, responseEdited: false);

        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(200),
            "BeforeResponse must not sleep on HTTP/2: it serializes parallel streams and stalls the frame loop.");
        Assert.AreNotSame(original, session.HttpClient.Response,
            "Bandwidth is paid by a streamed replacement that Core writes off the frame loop.");
        Assert.IsNotNull(session.HttpClient.Response.StreamBodyWriter);
    }

    [TestMethod]
    public async Task Http2Response_SmallBodies_AreStreamedToo_BecauseThereIsNoInlineBurstFallback()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = NetworkThrottle.Slow3G,
        };
        using var session = MakeHttp2Session(bodyLength: 1000);
        var original = session.HttpClient.Response;

        await interception.ApplyResponseThrottleAsync(session, responseEdited: false);

        Assert.AreNotSame(original, session.HttpClient.Response);
        Assert.IsNotNull(session.HttpClient.Response.StreamBodyWriter);
    }

    [TestMethod]
    public async Task Http2Response_WhenCoreRefusesTheReplacement_IsLeftUnshaped_WithoutSleepingOrThrowing()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = NetworkThrottle.Slow3G,
        };
        using var session = MakeHttp2Session(bodyLength: 100 * 1024);
        var original = session.HttpClient.Response;
        original.Locked = true; // already on its way to the client: Core refuses Respond

        var started = DateTime.UtcNow;
        await interception.ApplyResponseThrottleAsync(session, responseEdited: false);

        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(200));
        Assert.AreSame(original, session.HttpClient.Response);
    }

    [TestMethod]
    public async Task Http1Response_WhenCoreRefusesTheReplacement_FallsBackToOneBandwidthWait()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = new NetworkThrottleProfile("bw", latencyMs: 0, bytesPerSecond: 200_000),
        };
        using var session = MakeHttp2Session(bodyLength: 100 * 1024, major: 1);
        session.HttpClient.Response.Locked = true;

        var started = DateTime.UtcNow;
        await interception.ApplyResponseThrottleAsync(session, responseEdited: false);

        Assert.IsTrue(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(350),
            "100 KiB at 200 kB/s is about half a second.");
    }

    [TestMethod]
    public async Task Http2Request_PaysTheExchangeLatency_OnTheRequestSide()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = new NetworkThrottleProfile("lat", latencyMs: 300, bytesPerSecond: 0),
        };
        using var session = MakeHttp2Session(bodyLength: 10);
        var pace = typeof(InterceptionService).GetMethod(
            "PaceBufferedUploadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var started = DateTime.UtcNow;
        await (Task)pace.Invoke(interception, [session])!;

        Assert.IsTrue(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250));
    }

    [TestMethod]
    public async Task Http1Response_StillPaysLatencyInline()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            ThrottleProfile = new NetworkThrottleProfile("lat", latencyMs: 300, bytesPerSecond: 0),
        };
        using var session = MakeHttp2Session(bodyLength: 1000, major: 1);

        var started = DateTime.UtcNow;
        await interception.ApplyResponseThrottleAsync(session, responseEdited: false);

        Assert.IsTrue(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250));
    }

    private static SessionEventArgs MakeHttp2Session(int bodyLength, int major = 2)
    {
        var session = MakeSession();
        var version = new Version(major, major == 2 ? 0 : 1);
        var request = session.HttpClient.Request;
        request.HttpVersion = version;
        request.Method = "GET";
        request.Locked = true;
        var response = session.HttpClient.Response;
        response.HttpVersion = version;
        response.StatusCode = 200;
        response.ContentLength = bodyLength;
        response.IsBodyRead = true;
        response.Body = new byte[bodyLength];
        return session;
    }

    [TestMethod]
    public void ProxyTimeouts_StayDisabledByDefault()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Assert.AreEqual(0, proxy.ResponseHeaderTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleReadTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleWriteTimeoutSeconds);
        Assert.AreEqual(0, proxy.RequestTimeoutSeconds);
    }

    private static SessionEventArgs MakeSession()
    {
        var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }
}
