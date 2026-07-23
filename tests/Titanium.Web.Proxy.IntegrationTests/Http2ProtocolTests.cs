using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/2 stream-lifecycle bookkeeping (RST_STREAM/GOAWAY driven cancellation and
///     stream-registry cleanup, tracked per-stream by <c>Http2StreamState</c>/<c>Http2ConnectionState</c> in
///     <c>Http2Helper.CopyHttp2FrameAsync</c>) and connection-level frame validation (frame size, SETTINGS
///     ACK framing, WINDOW_UPDATE increment) that a real <see cref="System.Net.Http.SocketsHttpHandler" />
///     either never triggers or has no public API to deliberately trigger. Uses <see cref="Http2RawClient" />
///     and <see cref="Http2RawOriginServer" /> for byte-level control, as in
///     <see cref="Http2TrailerInterimContinuationTests" />.
/// </summary>
[TestClass]
public class Http2ProtocolTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        using var dummyProxy = new ProxyServer(false, false, false);
        dummyProxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        return dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
    }

    /// <summary>
    ///     A minimal origin handler for tests that only exercise the client-facing enforcement path (frame
    ///     validation replies sent directly back to the client leg) and never need the origin to answer a
    ///     real request: sends the mandatory initial SETTINGS, then reads (and discards) whatever the proxy
    ///     forwards until the connection closes, so the relay's server-leg read never faults on an unread,
    ///     abandoned socket.
    /// </summary>
    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler()
    {
        return async connection =>
        {
            await connection.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    await connection.ReadFrameAsync();
                }
            }
            catch
            {
                // connection torn down by the test/proxy - nothing further to do.
            }
        };
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_RstStream_From_Origin_Is_Relayed_And_Connection_Remains_Usable_For_Further_Streams()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();

            var (firstStreamId, _, _) = await connection.ReadRequestAsync();
            await connection.WriteFrameAsync(Http2FrameType.RstStream, firstStreamId, 0,
                Encode32((int)Http2ErrorCode.Cancel));

            var (secondStreamId, _, _) = await connection.ReadRequestAsync();
            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(secondStreamId, responseHeaders, false);
            await connection.WriteFrameAsync(Http2FrameType.Data, secondStreamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("second-body"));
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await SendGetRequestAsync(rawClient, uri, 1);

        // the origin's own initial SETTINGS frame is relayed to the client too and may arrive before the
        // RST_STREAM; skip past any such control frames to find it.
        Http2RawFrame.Frame resetFrame;
        do
        {
            resetFrame = await rawClient.Connection.ReadFrameAsync();
        } while (resetFrame.Type != Http2FrameType.RstStream);

        Assert.AreEqual(1, resetFrame.StreamId);
        Assert.AreEqual((int)Http2ErrorCode.Cancel, BinaryPrimitives.ReadInt32BigEndian(resetFrame.Payload));

        // stream 1 having been reset (and removed from the connection's stream registry) must not corrupt
        // the connection or block subsequent multiplexed streams from completing normally.
        await SendGetRequestAsync(rawClient, uri, 3);

        var (_, secondResponseHeaders, secondEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", secondResponseHeaders.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(secondEndStream);

        var dataFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
        Assert.AreEqual("second-body", Encoding.ASCII.GetString(dataFrame.Payload));
        Assert.IsTrue((dataFrame.Flags & Http2FrameFlag.EndStream) != 0);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_GoAway_From_Origin_Causes_Local_Refusal_Of_New_Stream_Above_Last_Accepted_Id()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();

            var (streamId, _, _) = await connection.ReadRequestAsync();
            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, true);

            var goAwayPayload = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(goAwayPayload.AsSpan(0, 4), streamId);
            BinaryPrimitives.WriteInt32BigEndian(goAwayPayload.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
            await connection.WriteFrameAsync(Http2FrameType.GoAway, 0, 0, goAwayPayload);

            // the origin must never see a request for the stream the client opens after the GOAWAY - the
            // proxy is expected to refuse it locally. Keep draining so this task does not fault when the
            // test disposes the connection.
            try
            {
                while (true)
                {
                    await connection.ReadFrameAsync();
                }
            }
            catch
            {
                // expected once the test tears the connection down.
            }
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await SendGetRequestAsync(rawClient, uri, 1);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream);

        await SendGetRequestAsync(rawClient, uri, 3);

        // the origin's GOAWAY (relayed to the client) and the proxy's own local RST_STREAM refusal of
        // stream 3 can arrive in either order; keep reading until the refusal is seen.
        Http2RawFrame.Frame? refusal = null;
        for (var i = 0; i < 10 && refusal == null; i++)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == 3)
            {
                refusal = frame;
            }
        }

        Assert.IsNotNull(refusal, "The proxy never sent a local RST_STREAM refusing the post-GOAWAY stream.");
        Assert.AreEqual((int)Http2ErrorCode.RefusedStream, BinaryPrimitives.ReadInt32BigEndian(refusal!.Value.Payload));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_GoAway_From_Client_As_First_Frame_Is_Not_Treated_As_ProtocolError()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(NoOpOriginHandler());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, uri.Host,
            uri.Port, new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 });
        Assert.AreEqual(SslApplicationProtocol.Http2, tunnel.NegotiatedApplicationProtocol);

        // Real browsers routinely open a pooled/speculative HTTP/2 connection and then decide they no
        // longer need it, tearing it down by sending GOAWAY as literally the first frame after the
        // connection preface - without ever sending SETTINGS. RFC 7540 §6.8 explicitly permits GOAWAY at
        // any time, so the proxy must not treat this as a connection-level PROTOCOL_ERROR (regression test
        // for the ERROR-level "expected a SETTINGS frame immediately after the connection preface, got
        // GoAway" seen in production whenever this happened).
        await tunnel.SslStream.WriteAsync(Http2Helper.ConnectionPreface, 0, Http2Helper.ConnectionPreface.Length);
        var connection = new Http2RawFrame.Connection(tunnel.SslStream);
        await connection.WriteFrameAsync(Http2FrameType.GoAway, 0, 0, new byte[8]);

        // Whatever the proxy does next (relay the origin's own SETTINGS frame, send its own graceful
        // GOAWAY, or simply close the connection) it must never respond with a GOAWAY carrying
        // PROTOCOL_ERROR. Once the client has said it is going away and sends nothing further, the proxy
        // has no reason to send anything more either (the relay just idles waiting for either leg to
        // produce more data) - so unlike the other tests in this file, there is no frame guaranteed to
        // eventually arrive here; each read below is bounded by a timeout, and a timeout is treated the
        // same as a clean close: both are valid proof that the proxy never reacted with PROTOCOL_ERROR.
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var readTask = connection.ReadFrameAsync();
                var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)));
                if (completed != readTask)
                {
                    // Nothing more arrived within the timeout - the proxy is simply idling, which is fine.
                    break;
                }

                var frame = await readTask;
                if (frame.Type == Http2FrameType.GoAway)
                {
                    var errorCode = (Http2ErrorCode)BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
                    Assert.AreNotEqual(Http2ErrorCode.ProtocolError, errorCode,
                        "The proxy must not treat a client GOAWAY-as-first-frame as a protocol violation.");
                }
            }
        }
        catch (IOException)
        {
            // The connection simply closing (rather than the proxy sending anything further) is an
            // equally valid outcome once the client has already said it is going away.
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Oversized_Frame_From_Client_Triggers_GoAway_With_FrameSizeError()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(NoOpOriginHandler());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Larger than Http2Helper's fixed 16384-byte MaxAcceptableFrameSize - a connection-level
        // FRAME_SIZE_ERROR regardless of frame type or whether the stream id is in use.
        var oversizedPayload = new byte[16384 + 1];
        try
        {
            await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, oversizedPayload);
        }
        catch (IOException)
        {
            // The proxy can detect the invalid declared length from the 9-byte frame header alone and react
            // (GOAWAY + connection close) before this client finishes streaming the oversized payload it
            // declared, which surfaces here as the write itself failing rather than a clean GOAWAY response
            // being available to read afterwards. Either outcome equally proves the connection was torn down
            // in response to the oversized frame, so there is nothing further to assert.
            return;
        }

        var frame = await ReadNonSettingsFrameAsync(rawClient);
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Settings_Ack_With_NonZero_Length_Triggers_GoAway_With_FrameSizeError()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(NoOpOriginHandler());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // RFC 7540 §6.5: a SETTINGS frame with the ACK flag set must have a zero-length payload.
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack, new byte[6]);

        var frame = await ReadNonSettingsFrameAsync(rawClient);
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_WindowUpdate_Zero_Increment_On_Stream_Triggers_RstStream_With_ProtocolError()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(NoOpOriginHandler());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // RFC 7540 §6.9.1: a zero increment on a stream-level WINDOW_UPDATE is a stream PROTOCOL_ERROR.
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, 0, Encode32(0));

        var frame = await ReadNonSettingsFrameAsync(rawClient);
        Assert.AreEqual(Http2FrameType.RstStream, frame.Type);
        Assert.AreEqual(1, frame.StreamId);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, BinaryPrimitives.ReadInt32BigEndian(frame.Payload));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_WindowUpdate_Zero_Increment_On_Connection_Triggers_GoAway_With_ProtocolError()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(NoOpOriginHandler());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // RFC 7540 §6.9.1: a zero increment on the connection-level (stream id 0) WINDOW_UPDATE is a
        // connection PROTOCOL_ERROR.
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, Encode32(0));

        var frame = await ReadNonSettingsFrameAsync(rawClient);
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    private static byte[] Encode32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    /// <summary>
    ///     Reads frames off <paramref name="rawClient" />, discarding any leading SETTINGS frames, and returns
    ///     the first non-SETTINGS one. The proxy always sends its own initial SETTINGS frame to the client
    ///     independently of - and racing with - whatever error/reset frame a deliberately invalid client frame
    ///     provokes (and may also ACK the client's own initial SETTINGS the same way), so a single unconditional
    ///     <see cref="Http2RawFrame.Connection.ReadFrameAsync" /> call is not deterministic; see the identical
    ///     reasoning already applied to <see cref="Http2_RstStream_From_Origin_Is_Relayed_And_Connection_Remains_Usable_For_Further_Streams" />
    ///     and <see cref="Http2_GoAway_From_Origin_Causes_Local_Refusal_Of_New_Stream_Above_Last_Accepted_Id" />.
    /// </summary>
    private static async Task<Http2RawFrame.Frame> ReadNonSettingsFrameAsync(Http2RawClient rawClient)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await rawClient.Connection.ReadFrameAsync();
        } while (frame.Type == Http2FrameType.Settings);

        return frame;
    }

    private static async Task SendGetRequestAsync(Http2RawClient rawClient, Uri uri, int streamId)
    {
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(streamId, requestHeaders, true);
    }
}
