using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression for Chrome <c>ERR_HTTP2_PROTOCOL_ERROR</c> (RST_STREAM error code 1) when Inspector
///     (or any MITM subscriber) attaches <c>OnResponseBodyWrite</c>/<c>OnRequestBodyWrite</c> without
///     buffering the whole body. MITM re-encodes HEADERS onto the dedicated frame-writer FIFO while the
///     body-write hook used to emit DATA with a direct locked socket write. DATA could overtake HEADERS
///     on the wire — RFC 7540 DATA on an idle stream is PROTOCOL_ERROR. Release builds made the race
///     easier to hit because the frame loop admits HEADERS+DATA back-to-back before the writer drain
///     runs. Leftover root CAs from a previous install produce certificate errors, not this symptom.
/// </summary>
[TestClass]
public class Http2BodyWriteHookFrameOrderTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Mitm_OnResponseBodyWrite_DoesNot_Send_Data_Before_Headers()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var responseHeaders = connection.EncodeHeaders(
                new[] { (":status", "200") },
                new[] { ("content-type", "text/plain") });
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var testSuite = new TestSuite();
        var proxy = SubscribeInspectorLikeHooks(testSuite.GetProxy());

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await SendGetAsync(rawClient, uri, 1);

        var (firstStreamFrame, body) = await ReadStreamFramesUntilEndAsync(rawClient, 1);
        Assert.AreEqual(Http2FrameType.Headers, firstStreamFrame.Type,
            "DATA must not precede HEADERS on the client socket (idle-stream PROTOCOL_ERROR).");
        Assert.AreEqual("ok", Encoding.ASCII.GetString(body));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Mitm_OnResponseBodyWrite_204_EmptyData_DoesNot_Precede_Headers()
    {
        // Google ads/collect often answers 204. Inspector does not call GetResponseBody (HasBody is
        // false) but still has OnResponseBodyWrite subscribed, so an origin that sends HEADERS then
        // an empty DATA END_STREAM used to be able to emit DATA first.
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var responseHeaders = connection.EncodeHeaders(
                new[] { (":status", "204") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Array.Empty<byte>());
        });

        using var testSuite = new TestSuite();
        var proxy = SubscribeInspectorLikeHooks(testSuite.GetProxy());

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await SendGetAsync(rawClient, uri, 1);

        var (firstStreamFrame, _) = await ReadStreamFramesUntilEndAsync(rawClient, 1);
        Assert.AreEqual(Http2FrameType.Headers, firstStreamFrame.Type,
            "Empty DATA for a 204 must not precede HEADERS (Chrome ERR_HTTP2_PROTOCOL_ERROR).");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Mitm_OnRequestBodyWrite_DoesNot_Send_Data_Before_Headers()
    {
        var originSawHeadersFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();

            Http2FrameType? firstStreamFrame = null;
            int streamId = -1;
            while (true)
            {
                var frame = await connection.ReadFrameAsync();
                if (frame.StreamId == 0)
                    continue;

                firstStreamFrame ??= frame.Type;
                if (streamId < 0 && frame.Type == Http2FrameType.Headers)
                    streamId = frame.StreamId;

                if (frame.Type is Http2FrameType.Data or Http2FrameType.Headers
                    && (frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }

            originSawHeadersFirst.TrySetResult(firstStreamFrame == Http2FrameType.Headers);

            if (streamId > 0)
            {
                var responseHeaders = connection.EncodeHeaders(
                    new[] { (":status", "204") },
                    Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: true);
            }
        });

        using var testSuite = new TestSuite();
        var proxy = SubscribeInspectorLikeHooks(testSuite.GetProxy());

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "POST"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            new[] { ("content-type", "text/plain") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, endStream: false);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Encoding.ASCII.GetBytes("body"));

        var completed = await Task.WhenAny(originSawHeadersFirst.Task, Task.Delay(5000));
        Assert.AreSame(originSawHeadersFirst.Task, completed, "Origin never finished reading the POST.");
        Assert.IsTrue(await originSawHeadersFirst.Task,
            "Request DATA must not precede HEADERS toward the origin (idle-stream PROTOCOL_ERROR).");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Mitm_BeforeResponse_GetResponseBody_With_BodyWriteHook_Completes()
    {
        // Inspector's BeforeResponse awaits GetResponseBody for inspectable bodies. The frame loop must keep
        // routing the origin's DATA while that handler is parked (response DATA must not be gated on the
        // raw handler task, which only completes after the body arrives).
        var payload = new string('a', 4096);
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var responseHeaders = connection.EncodeHeaders(
                new[] { (":status", "200") },
                new[] { ("content-type", "text/plain") });
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0,
                Encoding.ASCII.GetBytes(payload[..2048]));
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes(payload[2048..]));
        });

        using var testSuite = new TestSuite();
        var proxy = SubscribeInspectorLikeHooks(testSuite.GetProxy());
        proxy.BeforeResponse += async (_, e) => { await e.GetResponseBody(); };

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await SendGetAsync(rawClient, uri, 1);

        var (_, body) = await ReadStreamFramesUntilEndAsync(rawClient, 1);
        Assert.AreEqual(payload, Encoding.ASCII.GetString(body));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Mitm_OnResponseBodyWrite_Body_Larger_Than_Client_Window_Completes()
    {
        // The hook path must not park the frame loop on flow control AND must not lose the deferred tail:
        // END_STREAM parked behind a short client window used to be closed (RemoveStream cancels the
        // deferred queue and drops the flow window) so the client never saw the end of the body.
        const int total = 300 * 1024;
        var expected = new byte[total];
        new Random(7).NextBytes(expected);

        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var responseHeaders = connection.EncodeHeaders(
                new[] { (":status", "200") },
                new[] { ("content-type", "application/octet-stream") });
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: false);
            for (var offset = 0; offset < total; offset += 16384)
            {
                var len = Math.Min(16384, total - offset);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId,
                    offset + len >= total ? Http2FrameFlag.EndStream : 0,
                    expected.AsMemory(offset, len).ToArray());
            }
        });

        using var testSuite = new TestSuite();
        var proxy = SubscribeInspectorLikeHooks(testSuite.GetProxy());

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await SendGetAsync(rawClient, uri, 1);

        var received = new MemoryStream();
        var ended = false;
        for (var i = 0; i < 2048 && !ended; i++)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == 1)
                Assert.Fail("Stream was reset before the body completed.");
            if (frame.Type != Http2FrameType.Data || frame.StreamId != 1)
                continue;

            received.Write(frame.Payload, 0, frame.Payload.Length);
            ended = (frame.Flags & Http2FrameFlag.EndStream) != 0;
            if (!ended && frame.Payload.Length > 0)
            {
                // Replenish the (default 65535-byte) windows as a real client does.
                var increment = new byte[]
                {
                    (byte)(frame.Payload.Length >> 24), (byte)(frame.Payload.Length >> 16),
                    (byte)(frame.Payload.Length >> 8), (byte)frame.Payload.Length
                };
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, increment);
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, 0, increment);
            }
        }

        Assert.IsTrue(ended, "Client never received END_STREAM for a body larger than its window.");
        CollectionAssert.AreEqual(expected, received.ToArray());
    }

    private static ProxyServer SubscribeInspectorLikeHooks(ProxyServer proxy)
    {
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        // Inspector always attaches these, including when the throttle profile is None.
        proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        proxy.BeforeResponse += (_, _) => Task.CompletedTask;
        proxy.OnRequestBodyWrite += (_, _) => Task.CompletedTask;
        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;
        return proxy;
    }

    private static Task SendGetAsync(Http2RawClient rawClient, Uri uri, int streamId)
    {
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        return rawClient.Connection.WriteHeaderBlockAsync(streamId, requestHeaders, true);
    }

    private static async Task<(Http2RawFrame.Frame FirstStreamFrame, byte[] Body)> ReadStreamFramesUntilEndAsync(
        Http2RawClient rawClient, int streamId)
    {
        Http2RawFrame.Frame? first = null;
        var body = new MemoryStream();
        for (var i = 0; i < 64; i++)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId)
            {
                var code = (frame.Payload[0] << 24) | (frame.Payload[1] << 16) |
                           (frame.Payload[2] << 8) | (frame.Payload[3]);
                Assert.Fail($"Stream {streamId} was reset with {(Http2ErrorCode)code} before completing.");
            }

            if (frame.Type == Http2FrameType.GoAway)
            {
                var code = (frame.Payload[4] << 24) | (frame.Payload[5] << 16) |
                           (frame.Payload[6] << 8) | (frame.Payload[7]);
                Assert.Fail($"Connection GOAWAY with {(Http2ErrorCode)code} before stream {streamId} completed.");
            }

            if (frame.StreamId != streamId)
                continue;
            if (frame.Type is Http2FrameType.WindowUpdate or Http2FrameType.Priority)
                continue;

            first ??= frame;
            if (frame.Type == Http2FrameType.Data && first.Value.Type != Http2FrameType.Headers)
            {
                Assert.Fail("DATA arrived on the stream before HEADERS (idle-stream PROTOCOL_ERROR).");
            }

            if (frame.Type == Http2FrameType.Data)
                body.Write(frame.Payload, 0, frame.Payload.Length);

            if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                return (first.Value, body.ToArray());
        }

        Assert.Fail($"Stream {streamId} never received END_STREAM.");
        return default;
    }
}
