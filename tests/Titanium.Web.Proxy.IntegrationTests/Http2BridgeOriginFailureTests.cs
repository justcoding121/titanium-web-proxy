using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07 ledger items 5 and 6: an h2 client bridged to an HTTP/1.1 origin must see a valid 5xx
///     (502 for unreachable / dropped, 504 for a deadline) when nothing was committed, an <c>RST_STREAM</c> (never a
///     "complete" truncated body) once headers were sent, and replays on a stale pooled connection are single-shot
///     and limited to bodiless idempotent requests.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2BridgeOriginFailureTests
{
    private sealed record Outcome(string? Status, int BodyBytes, bool EndedCleanly, Http2ErrorCode? Rst);

    private static async Task<(ProxyServer Proxy, TestSuite Suite)> StartBridgeProxyAsync(Action<ProxyServer>? configure = null)
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        configure?.Invoke(proxy);
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        await Task.CompletedTask;
        return (proxy, suite);
    }

    private static async Task SendRequestAsync(Http2RawClient raw, int streamId, string method,
        string[]? bodyPieces = null, bool declareLength = true)
    {
        var hasBody = bodyPieces is { Length: > 0 };
        var pseudo = new[]
        {
            (":method", method), (":scheme", "https"), (":authority", "localhost"), (":path", "/"),
        };
        var regular = hasBody && declareLength
            ? new[] { ("content-length", bodyPieces!.Sum(p => p.Length).ToString()) }
            : Array.Empty<(string, string)>();

        var block = raw.Connection.EncodeHeaders(pseudo, regular);
        await raw.Connection.WriteHeaderBlockAsync(streamId, block, endStream: !hasBody);
        if (!hasBody) return;

        for (var i = 0; i < bodyPieces!.Length; i++)
        {
            var last = i == bodyPieces.Length - 1;
            await raw.Connection.WriteFrameAsync(Http2FrameType.Data, streamId,
                last ? Http2FrameFlag.EndStream : (Http2FrameFlag)0, Encoding.ASCII.GetBytes(bodyPieces[i]));
        }
    }

    private static async Task<Outcome> ReadOutcomeAsync(Http2RawClient raw, int streamId)
    {
        string? status = null;
        var bodyBytes = 0;
        while (true)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (frame.Type == Http2FrameType.GoAway)
                return new Outcome(status, bodyBytes, false, (Http2ErrorCode)ReadInt32(frame.Payload, 4));

            if (frame.StreamId != streamId) continue;

            switch (frame.Type)
            {
                case Http2FrameType.RstStream:
                    return new Outcome(status, bodyBytes, false, (Http2ErrorCode)ReadInt32(frame.Payload, 0));
                case Http2FrameType.Headers:
                    status = raw.Connection.DecodeHeaders(frame.Payload).Single(h => h.Name == ":status").Value;
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        return new Outcome(status, bodyBytes, true, null);
                    break;
                case Http2FrameType.Data:
                    bodyBytes += frame.Payload.Length;
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        return new Outcome(status, bodyBytes, true, null);
                    break;
            }
        }
    }

    private static int ReadInt32(byte[] p, int offset) =>
        (p[offset] << 24) | (p[offset + 1] << 16) | (p[offset + 2] << 8) | p[offset + 3];

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_OriginRefused_Returns502()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var refusedPort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var (proxy, suite) = await StartBridgeProxyAsync();
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", refusedPort);

        await SendRequestAsync(raw, 1, "GET");
        var outcome = await ReadOutcomeAsync(raw, 1);

        Assert.AreEqual("502", outcome.Status);
        Assert.IsNull(outcome.Rst);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_ResponseHeaderTimeout_Returns504()
    {
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : OriginAction.NeverRespond);

        var (proxy, suite) = await StartBridgeProxyAsync(p => p.ResponseHeaderTimeoutSeconds = 1);
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        await SendRequestAsync(raw, 1, "GET");
        var outcome = await ReadOutcomeAsync(raw, 1);

        Assert.AreEqual("504", outcome.Status);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_HeadersAlreadySent_OriginDrops_SendsRstStream_NotCompleteBody()
    {
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : OriginAction.PartialBodyThenClose);

        var (proxy, suite) = await StartBridgeProxyAsync();
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        await SendRequestAsync(raw, 1, "GET");
        var outcome = await ReadOutcomeAsync(raw, 1);

        // Either the 200 was never committed (a clean 502), or it was and the stream must be reset: a clean
        // END_STREAM after 7 of 100 declared bytes would present a truncated body as complete.
        if (outcome.Status == "200")
        {
            Assert.IsFalse(outcome.EndedCleanly,
                $"truncated origin body ({outcome.BodyBytes} of 100 bytes) must not end the h2 stream cleanly");
            Assert.IsTrue(outcome.Rst is Http2ErrorCode.Cancel or Http2ErrorCode.InternalError
                    or Http2ErrorCode.RefusedStream, $"unexpected RST code {outcome.Rst}");
        }
        else
        {
            Assert.AreEqual("502", outcome.Status);
        }
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_FreshConnectionEof_NotReplayed()
    {
        // The origin drops the very first request on a brand-new connection: nothing was reused, so there is no
        // stale-pool explanation and the request must not be silently replayed.
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : OriginAction.CloseWithoutResponse);

        var (proxy, suite) = await StartBridgeProxyAsync();
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        await SendRequestAsync(raw, 1, "GET");
        var outcome = await ReadOutcomeAsync(raw, 1);

        Assert.AreEqual("502", outcome.Status);
        Assert.AreEqual(1, origin.Requests, "a fresh connection must not trigger the stale-connection replay");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_ReplayCappedAtOne()
    {
        // conn 1 answers once then goes stale; every later connection drops the request. The stale GET is replayed
        // exactly once (conn 2) and then fails with 502: 3 requests in total, never a retry loop.
        using var origin = new ScriptedTlsOrigin((conn, request) =>
            request == -1 ? OriginAction.Respond
            : conn == 1 && request == 0 ? OriginAction.Respond
            : OriginAction.CloseWithoutResponse);

        var (proxy, suite) = await StartBridgeProxyAsync();
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        await SendRequestAsync(raw, 1, "GET");
        Assert.AreEqual("200", (await ReadOutcomeAsync(raw, 1)).Status, "warm-up must succeed");
        await Task.Delay(300);

        await SendRequestAsync(raw, 3, "GET");
        var outcome = await ReadOutcomeAsync(raw, 3);

        Assert.AreEqual("502", outcome.Status);
        Assert.AreEqual(3, origin.Requests, "warm-up + stale attempt + exactly one replay");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task Bridge_StreamedBody_NotReplayed()
    {
        // A request whose body was streamed from the client (no content-length, several DATA frames) cannot be
        // re-sent: the stale connection yields a clean 502 and no second origin request.
        using var origin = new ScriptedTlsOrigin((conn, request) =>
            request == -1 ? OriginAction.Respond
            : conn == 1 && request == 0 ? OriginAction.Respond
            : conn == 1 ? OriginAction.CloseWithoutResponse
            : OriginAction.Respond);

        var (proxy, suite) = await StartBridgeProxyAsync();
        using var _ = suite;
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        await SendRequestAsync(raw, 1, "GET");
        Assert.AreEqual("200", (await ReadOutcomeAsync(raw, 1)).Status, "warm-up must succeed");
        await Task.Delay(300);

        await SendRequestAsync(raw, 3, "PUT", new[] { "ab", "cd" }, declareLength: false);
        var outcome = await ReadOutcomeAsync(raw, 3);

        Assert.AreEqual("502", outcome.Status);
        Assert.AreEqual(2, origin.Requests, "warm-up + the failed streamed request; no replay");
    }
}
