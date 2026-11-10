using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Behavior tests for the h2 to h2 DATA relay in <c>Http2Helper.Copy.cs</c>: send-window accounting,
///     deferred (parked) DATA, padded DATA, empty END_STREAM DATA, and the per-stream deferred-bytes cap.
///     A raw, flow-control-aware client and origin (<see cref="Http2FlowPeer" />) sit on either side of the
///     proxy so the tests control exactly when credit is granted.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2RelayFlowControlAndPaddingTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + seed + (i >> 8)) & 0xff);
        return data;
    }

    private static IEnumerable<(string, string)> RequestPseudo(Uri uri, string method, string path = "/") =>
    [
        (":method", method), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", path)
    ];

    private static string Status(IEnumerable<(string Name, string Value)> headers) =>
        headers.First(h => h.Name == ":status").Value;

    // Bug class: the relay writes more DATA to a client than the client's flow-control window allows
    // (or drops the parked remainder when the window is exhausted), so large downloads to a slow
    // consumer overrun the client or truncate.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Response_Larger_Than_Client_Window_Respects_Window_And_Delivers_Intact_On_Credit()
    {
        const int total = 220_000;
        var expected = Pattern(total);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var originDone = new TaskCompletionSource();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            var req = await peer.ReadMessageAsync(autoCredit: true, Step);
            await peer.SendHeadersAsync(req.StreamId, [(":status", "200")],
                [("content-type", "application/octet-stream")], endStream: false);
            await peer.SendDataAsync(req.StreamId, expected, endStream: true, Step);
            await originDone.Task.WaitAsync(Step);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "GET"), [], endStream: true);

        // Never grant credit beyond the default 65535-byte windows and watch what arrives.
        var received = new MemoryStream();
        var sawEnd = false;
        var quietSince = DateTime.UtcNow;
        List<(string Name, string Value)>? responseHeaders = null;
        while (DateTime.UtcNow - quietSince < TimeSpan.FromSeconds(2))
        {
            var frame = await client.ReadFrameAsync(TimeSpan.FromMilliseconds(500));
            if (frame == null) continue;
            if (frame.Value.Type == Http2FrameType.Headers && frame.Value.StreamId == 1)
                responseHeaders = client.Connection.DecodeHeaders(frame.Value.Payload);
            if (frame.Value.Type != Http2FrameType.Data) continue;
            quietSince = DateTime.UtcNow;
            received.Write(frame.Value.Payload);
            sawEnd |= (frame.Value.Flags & Http2FrameFlag.EndStream) != 0;
        }

        Assert.IsNotNull(responseHeaders, "Response HEADERS must be relayed before the body stalls.");
        Assert.AreEqual("200", Status(responseHeaders));
        Assert.IsTrue(received.Length > 0, "Some body must flow within the initial window.");
        Assert.IsTrue(received.Length <= 65535,
            $"The relay sent {received.Length} bytes but the client only granted 65535.");
        Assert.IsFalse(sawEnd, "END_STREAM must not overtake the parked body bytes.");

        await client.SendWindowUpdateAsync(0, 1_000_000);
        await client.SendWindowUpdateAsync(1, 1_000_000);
        var deadline = DateTime.UtcNow + Step;
        while (!sawEnd && DateTime.UtcNow < deadline)
        {
            var frame = await client.ReadFrameAsync(TimeSpan.FromSeconds(1));
            if (frame is not { Type: Http2FrameType.Data } data || data.StreamId != 1) continue;
            received.Write(data.Payload);
            sawEnd = (data.Flags & Http2FrameFlag.EndStream) != 0;
        }

        originDone.TrySetResult();
        Assert.IsTrue(sawEnd, "The parked remainder and END_STREAM must be delivered once credit arrives.");
        CollectionAssert.AreEqual(expected, received.ToArray(), "Body must arrive byte-for-byte intact.");
    }

    // Bug class: padded DATA from the origin has its pad-length byte or padding mis-handled so the
    // client sees a corrupted or shifted body.
    [TestMethod]
    [Timeout(30_000)]
    public async Task Padded_Origin_Data_Is_Relayed_Without_Corrupting_Body()
    {
        var body = Pattern(3000, seed: 7);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var originDone = new TaskCompletionSource();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            var req = await peer.ReadMessageAsync(autoCredit: true, Step);
            await peer.SendHeadersAsync(req.StreamId, [(":status", "200")], [], endStream: false);
            await peer.SendPaddedDataAsync(req.StreamId, body[..1000], padLength: 40, endStream: false, Step);
            await peer.SendPaddedDataAsync(req.StreamId, body[1000..], padLength: 255, endStream: true, Step);
            await originDone.Task.WaitAsync(Step);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "GET"), [], endStream: true);
        var message = await client.ReadMessageAsync(autoCredit: true, Step);
        originDone.TrySetResult();

        Assert.AreEqual("200", Status(message.Headers));
        CollectionAssert.AreEqual(body, message.Body, "Padding must be excluded from, and never corrupt, the body.");
    }

    // Bug class: padded request DATA from the client is forwarded with the wrong length / pad byte so
    // the origin reads a corrupted upload body.
    [TestMethod]
    [Timeout(30_000)]
    public async Task Padded_Client_Data_Reaches_Origin_Without_Corrupting_Body()
    {
        var body = Pattern(2500, seed: 3);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var originSaw = new TaskCompletionSource<byte[]>();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            var req = await peer.ReadMessageAsync(autoCredit: true, Step);
            originSaw.TrySetResult(req.Body);
            await peer.SendHeadersAsync(req.StreamId, [(":status", "204")], [], endStream: true);
            await Task.Delay(500);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "POST", "/upload"),
            [("content-length", body.Length.ToString())], endStream: false);
        await client.SendPaddedDataAsync(1, body[..900], padLength: 17, endStream: false, Step);
        await client.SendPaddedDataAsync(1, body[900..], padLength: 100, endStream: true, Step);

        var seen = await originSaw.Task.WaitAsync(Step);
        CollectionAssert.AreEqual(body, seen, "Origin must read exactly the unpadded upload bytes.");
        var response = await client.ReadMessageAsync(autoCredit: true, Step);
        Assert.AreEqual("204", Status(response.Headers));
    }

    // Bug class: a request ended by an empty DATA frame with END_STREAM (what many clients send after
    // HEADERS) never closes the request half toward the origin, so the origin waits forever.
    [TestMethod]
    [Timeout(30_000)]
    public async Task Empty_EndStream_Data_Closes_Request_Toward_Origin()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            var req = await peer.ReadMessageAsync(autoCredit: true, Step);
            await peer.SendHeadersAsync(req.StreamId, [(":status", "200")],
                [("x-request-body-length", req.Body.Length.ToString())], endStream: true);
            await Task.Delay(500);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "GET"), [], endStream: false);
        await client.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Array.Empty<byte>());

        var response = await client.ReadMessageAsync(autoCredit: true, Step);
        Assert.AreEqual("200", Status(response.Headers));
        Assert.AreEqual("0", response.Headers.First(h => h.Name == "x-request-body-length").Value);
    }

    // Bug class: when the origin withholds window credit the relay either overruns the origin window
    // or loses the parked upload bytes instead of delivering them intact once credit is granted.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Request_Larger_Than_Origin_Window_Is_Parked_Then_Delivered_Intact_On_Credit()
    {
        const int total = 150_000;
        var body = Pattern(total, seed: 11);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var beforeCredit = new TaskCompletionSource<int>();
        var release = new TaskCompletionSource();
        var originBody = new TaskCompletionSource<byte[]>();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            var received = new MemoryStream();
            var streamId = -1;
            // Phase 1: read without ever granting credit until the stream goes quiet.
            while (true)
            {
                var frame = await peer.ReadFrameAsync(TimeSpan.FromMilliseconds(1500));
                if (frame == null) break;
                if (frame.Value.Type == Http2FrameType.Headers) streamId = frame.Value.StreamId;
                if (frame.Value.Type == Http2FrameType.Data) received.Write(frame.Value.Payload);
            }

            beforeCredit.TrySetResult((int)received.Length);
            await release.Task.WaitAsync(Step);
            await peer.SendWindowUpdateAsync(0, 1_000_000);
            await peer.SendWindowUpdateAsync(streamId, 1_000_000);
            var ended = false;
            while (!ended)
            {
                var frame = await peer.ReadFrameAsync(Step) ?? throw new TimeoutException("origin never saw END_STREAM");
                if (frame.Type != Http2FrameType.Data) continue;
                received.Write(frame.Payload);
                ended = (frame.Flags & Http2FrameFlag.EndStream) != 0;
            }

            originBody.TrySetResult(received.ToArray());
            await peer.SendHeadersAsync(streamId, [(":status", "200")], [], endStream: true);
            await Task.Delay(500);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "POST", "/big"),
            [("content-length", total.ToString())], endStream: false);
        await client.SendDataAsync(1, body, endStream: true, Step);

        var before = await beforeCredit.Task.WaitAsync(Step);
        Assert.IsTrue(before <= 65535, $"Origin received {before} bytes but never granted more than 65535.");
        release.TrySetResult();

        var seen = await originBody.Task.WaitAsync(Step);
        CollectionAssert.AreEqual(body, seen, "Parked upload bytes must be delivered intact after credit.");
        var response = await client.ReadMessageAsync(autoCredit: true, Step);
        Assert.AreEqual("200", Status(response.Headers));
    }

    // Bug class: a starved origin lets the parked request body grow without bound; the relay must reset
    // the stream (ENHANCE_YOUR_CALM) once the per-stream deferred-bytes cap is exceeded.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Deferred_Request_Bytes_Over_Cap_Reset_The_Stream()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var release = new TaskCompletionSource();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            // Read and discard; never grant stream credit so the proxy must park everything past 65535.
            try
            {
                while (true)
                {
                    var frame = await peer.ReadFrameAsync(Step);
                    if (frame == null || release.Task.IsCompleted) break;
                }
            }
            catch (IOException)
            {
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ResourceLimits = proxy.ResourceLimits.WithMaxDeferredOutboundBytesPerStream(20_000);
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1, RequestPseudo(uri, "POST", "/flood"), [], endStream: false);
        var chunk = Pattern(16384);
        Http2ErrorCode? reset = null;
        try
        {
            for (var i = 0; i < 12 && reset == null; i++)
            {
                await client.SendDataAsync(1, chunk, endStream: false, TimeSpan.FromSeconds(3));
                while (await client.ReadFrameAsync(TimeSpan.FromMilliseconds(50)) is { } f)
                    reset ??= ErrorOf(f);
            }
        }
        catch (TimeoutException)
        {
            // Window exhausted after the reset: fall through to the frame scan below.
        }
        catch (IOException)
        {
        }

        var deadline = DateTime.UtcNow + Step;
        while (reset == null && DateTime.UtcNow < deadline &&
               await client.ReadFrameAsync(TimeSpan.FromSeconds(1)) is { } frame)
            reset ??= ErrorOf(frame);

        release.TrySetResult();
        Assert.AreEqual(Http2ErrorCode.EnhanceYourCalm, reset,
            "Exceeding the deferred-bytes cap must reset the stream (or connection) with ENHANCE_YOUR_CALM.");

        static Http2ErrorCode? ErrorOf(Http2RawFrame.Frame f) => f.Type switch
        {
            Http2FrameType.RstStream when f.StreamId == 1 =>
                (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(f.Payload),
            Http2FrameType.GoAway => (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4)),
            _ => null
        };
    }
}
