using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     A document navigation that draws a bot-challenge 403 must reach the HTTP/2 client as a finished
///     meta-refresh (HEADERS + DATA/END_STREAM) followed by GOAWAY(NO_ERROR). Closing the socket while
///     those frames are still queued is what Chrome reports as ERR_HTTP2_PROTOCOL_ERROR.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2SeamlessDecryptBypassTests
{
    private const int RaceIterations = 200;

    [TestMethod]
    [Timeout(180 * 1000)]
    public async Task DocumentBotChallenge_DeliversMetaRefreshThenGoAway_AndNextConnectIsOpaque()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(ServeChallengeAsync);

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableDecryptFailureBypass = true;

        var proxyPort = proxy.ProxyEndPoints[0].Port;
        var originPort = origin.Port;

        await AssertMetaRefreshThenGoAwayAsync(proxyPort, originPort);
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("localhost"));
        await AssertOpaqueChallengeAsync(proxyPort, originPort);

        for (var i = 0; i < RaceIterations; i++)
        {
            Assert.IsTrue(proxy.RemoveDecryptFailureBypass("localhost"),
                $"iteration {i}: learned bypass was not active");
            await AssertMetaRefreshThenGoAwayAsync(proxyPort, originPort);
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ClientIgnoringGoAway_IsClosedByTheFallback()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(ServeChallengeAsync);

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableDecryptFailureBypass = true;

        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);
        var block = raw.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
            },
            new[] { ("sec-fetch-dest", "document"), ("accept", "text/html") });
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sawGoAway = false;
        try
        {
            // Never close from the client side: only the proxy's bounded fallback can end this connection.
            while (true)
            {
                var frame = await raw.Connection.ReadFrameAsync().WaitAsync(cts.Token);
                if (frame.Type == Http2FrameType.Settings && (frame.Flags & Http2FrameFlag.Ack) == 0)
                {
                    await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack,
                        Array.Empty<byte>());
                }
                else if (frame.Type == Http2FrameType.GoAway)
                {
                    sawGoAway = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the proxy never closed a connection whose client ignored GOAWAY");
        }
        catch (Exception) when (sawGoAway)
        {
            // EOF after GOAWAY: the fallback closed the connection.
        }

        Assert.IsTrue(sawGoAway, "connection ended before GOAWAY");
    }

    private static async Task ServeChallengeAsync(Http2RawFrame.Connection connection)
    {
        await connection.SendInitialSettingsAsync();
        try
        {
            while (true)
            {
                var (streamId, _, _) = await connection.ReadRequestAsync();
                var headers = connection.EncodeHeaders(
                    new[] { (":status", "403") },
                    new[]
                    {
                        ("set-cookie", "_abck=AFE1F267~-1~YAAQ; Path=/; Secure"),
                        ("content-type", "text/html; charset=utf-8")
                    });
                await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.UTF8.GetBytes("<html>blocked</html>"));
            }
        }
        catch
        {
            // Proxy or client closed the origin connection.
        }
    }

    private static async Task AssertMetaRefreshThenGoAwayAsync(int proxyPort, int originPort)
    {
        using var raw = await Http2RawClient.ConnectAsync(proxyPort, "localhost", originPort);
        var block = raw.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
            },
            new[] { ("sec-fetch-dest", "document"), ("accept", "text/html") });
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var body = new StringBuilder();
        var sawEndStream = false;
        string? status = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (true)
        {
            Http2RawFrame.Frame frame;
            try
            {
                frame = await raw.Connection.ReadFrameAsync().WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(sawEndStream
                    ? "timed out waiting for GOAWAY after END_STREAM"
                    : "connection ended or stalled before END_STREAM (truncated HTTP/2 response)");
                return;
            }
            catch (Exception ex)
            {
                Assert.Fail(sawEndStream
                    ? "connection closed after END_STREAM without GOAWAY: " + ex.Message
                    : "EOF before END_STREAM: " + ex.Message);
                return;
            }

            if (frame.Type == Http2FrameType.Settings && (frame.Flags & Http2FrameFlag.Ack) == 0)
            {
                await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack,
                    Array.Empty<byte>());
                continue;
            }

            if (frame.Type == Http2FrameType.RstStream)
            {
                Assert.Fail($"RST_STREAM on stream {frame.StreamId} before a clean GOAWAY");
            }

            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
            {
                var headers = raw.Connection.DecodeHeaders(frame.Payload);
                status = headers.Find(h => h.Name == ":status").Value;
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    sawEndStream = true;
                continue;
            }

            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
            {
                body.Append(Encoding.UTF8.GetString(frame.Payload));
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    sawEndStream = true;
                continue;
            }

            if (frame.Type == Http2FrameType.GoAway)
            {
                Assert.IsTrue(sawEndStream, "GOAWAY arrived before the meta-refresh stream ended");
                Assert.AreEqual("200", status);
                Assert.IsTrue(body.ToString().Contains("meta http-equiv=\"refresh\"", StringComparison.Ordinal),
                    body.ToString());
                Assert.IsTrue(frame.Payload.Length >= 8);
                var error = (frame.Payload[4] << 24) | (frame.Payload[5] << 16)
                    | (frame.Payload[6] << 8) | frame.Payload[7];
                Assert.AreEqual((int)Http2ErrorCode.NoError, error);
                var lastStream = (frame.Payload[0] << 24) | (frame.Payload[1] << 16)
                    | (frame.Payload[2] << 8) | frame.Payload[3];
                Assert.IsTrue(lastStream >= 1, "GOAWAY last-stream-id did not cover the document stream");
                return;
            }
        }
    }

    private static async Task AssertOpaqueChallengeAsync(int proxyPort, int originPort)
    {
        using var raw = await Http2RawClient.ConnectAsync(proxyPort, "localhost", originPort);
        var block = raw.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
            },
            new[] { ("sec-fetch-dest", "document"), ("accept", "text/html") });
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (headers, _, error) = await raw.Connection.ReadHeadersOrRstAsync(1).WaitAsync(cts.Token);
        Assert.AreEqual(Http2ErrorCode.NoError, error);
        Assert.IsNotNull(headers);
        Assert.AreEqual("403", headers!.Find(h => h.Name == ":status").Value,
            "learned host was decrypted again instead of tunneled opaque");
    }
}
