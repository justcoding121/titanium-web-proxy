using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.ProxySocket.Authentication;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2SyntheticFinalizeAndAuthUserPassTests
{
    private static SessionEventArgs MakeSession(ProxyServer? proxy = null)
    {
        proxy ??= new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version20;
        session.HttpClient.Request.Method = "GET";
        return session;
    }

    [TestMethod]
    public async Task EmitSyntheticResponseAsync_BufferedBody_WritesHeadersAndData()
    {
        using var session = MakeSession();
        session.Ok("hello-body");

        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(1, cts);
        connectionState.ClientSendFlow.RegisterStream(1);
        connectionState.ServerSettingsRelayed.SetResult(true);

        using var clientStream = new MemoryStream();
        await Http2Helper.EmitSyntheticResponseAsync(session, 1, connectionState, clientStream,
            CancellationToken.None);

        // Frames are queued on the client write chain; drain it before asserting the wire bytes.
        await connectionState.ClientWriteChain;

        Assert.IsTrue(session.HttpClient.Response.IsBodySent);
        Assert.IsTrue(clientStream.Length > 9);
        var wire = clientStream.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
    }

    [TestMethod]
    public async Task EmitSyntheticResponseAsync_EmptyBody_EndStreamOnHeaders()
    {
        using var session = MakeSession();
        session.Redirect("https://example.com/");

        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(1, cts);
        connectionState.ClientSendFlow.RegisterStream(3);
        connectionState.ServerSettingsRelayed.SetResult(true);

        using var clientStream = new MemoryStream();
        await Http2Helper.EmitSyntheticResponseAsync(session, 3, connectionState, clientStream,
            CancellationToken.None);

        // Frames are queued on the client write chain; drain it before asserting the wire bytes.
        await connectionState.ClientWriteChain;

        var wire = clientStream.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), wire[4]);
        Assert.IsFalse(ReadFrames(wire).Exists(f => f.Type == Http2FrameType.GoAway));
    }

    [TestMethod]
    public async Task EmitSyntheticResponseAsync_CloseAfterResponse_QueuesGoAwayAfterEndStream()
    {
        using var session = MakeSession();
        session.Ok("<html><meta http-equiv=\"refresh\"></html>");
        session.CloseClientConnectionAfterResponse = true;

        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(1, cts);
        connectionState.LastClientStreamId = 5;
        connectionState.ClientSendFlow.RegisterStream(1);
        connectionState.ServerSettingsRelayed.SetResult(true);

        using var clientStream = new MemoryStream();
        await Http2Helper.EmitSyntheticResponseAsync(session, 1, connectionState, clientStream,
            CancellationToken.None);
        await connectionState.ClientWriteChain;

        var frames = ReadFrames(clientStream.ToArray());
        var endStreamAt = frames.FindIndex(f => (f.Flags & (byte)Http2FrameFlag.EndStream) != 0);
        var goAwayAt = frames.FindIndex(f => f.Type == Http2FrameType.GoAway);
        Assert.IsTrue(endStreamAt >= 0, "synthetic response did not end the stream");
        Assert.IsTrue(goAwayAt > endStreamAt, "GOAWAY must follow END_STREAM on the writer FIFO");

        var goAway = frames[goAwayAt];
        Assert.AreEqual(0, goAway.StreamId);
        Assert.AreEqual(8, goAway.Payload.Length);
        Assert.AreEqual(5, (goAway.Payload[0] << 24) | (goAway.Payload[1] << 16)
            | (goAway.Payload[2] << 8) | goAway.Payload[3]);
        Assert.AreEqual((int)Http2ErrorCode.NoError,
            (goAway.Payload[4] << 24) | (goAway.Payload[5] << 16)
            | (goAway.Payload[6] << 8) | goAway.Payload[7]);
    }

    private static List<(Http2FrameType Type, int StreamId, byte Flags, byte[] Payload)> ReadFrames(byte[] wire)
    {
        var frames = new List<(Http2FrameType, int, byte, byte[])>();
        var pos = 0;
        while (pos + 9 <= wire.Length)
        {
            var length = (wire[pos] << 16) | (wire[pos + 1] << 8) | wire[pos + 2];
            if (pos + 9 + length > wire.Length)
                break;

            var type = (Http2FrameType)wire[pos + 3];
            var flags = wire[pos + 4];
            var streamId = ((wire[pos + 5] << 24) | (wire[pos + 6] << 16) | (wire[pos + 7] << 8) | wire[pos + 8])
                           & 0x7fffffff;
            var payload = new byte[length];
            Buffer.BlockCopy(wire, pos + 9, payload, 0, length);
            frames.Add((type, streamId, flags, payload));
            pos += 9 + length;
        }

        return frames;
    }

    [TestMethod]
    public async Task FinalizeStreamAsync_RunsOnce_AndSwallowsHandlerFaults()
    {
        using var session = MakeSession();
        var state = new Http2StreamState(1, session);
        var calls = 0;

        await Http2Helper.FinalizeStreamAsync(state, _ =>
        {
            calls++;
            throw new InvalidOperationException("handler boom");
        }, NullLogger.Instance);

        Assert.AreEqual(1, calls);

        // Second finalize is a no-op (session already disposed).
        await Http2Helper.FinalizeStreamAsync(state, _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, NullLogger.Instance);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task SendBody_WithBufferedBody_WritesHeadersAndDataFrames()
    {
        using var ms = new MemoryStream();
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var (header, buf) = (new Http2FrameHeader { StreamId = 1 }, new byte[9]);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            StatusDescription = "OK",
            Body = Encoding.ASCII.GetBytes("abcdef")
        };
        response.IsBodyRead = true;

        await Http2QueuedSend.SendBody(settings, response, header, buf, 16, flow, ms,
            CancellationToken.None);

        Assert.IsTrue(ms.Length > 9);
        Assert.AreEqual((byte)Http2FrameType.Headers, ms.ToArray()[3]);
    }

    [TestMethod]
    public void AuthUserPass_GetAuthenticationBytes_FormatsRfc1929Packet()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var auth = new AuthUserPass(socket, "alice", "secret");
        var length = (int)typeof(AuthUserPass).GetMethod("GetAuthenticationLength",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(auth, null)!;
        Assert.AreEqual(3 + 5 + 6, length);

        var buffer = new byte[length];
        typeof(AuthUserPass).GetMethod("GetAuthenticationBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(auth, [buffer.AsMemory()]);

        Assert.AreEqual(1, buffer[0]);
        Assert.AreEqual(5, buffer[1]);
        Assert.AreEqual("alice", Encoding.ASCII.GetString(buffer, 2, 5));
        Assert.AreEqual(6, buffer[7]);
        Assert.AreEqual("secret", Encoding.ASCII.GetString(buffer, 8, 6));
    }
}
