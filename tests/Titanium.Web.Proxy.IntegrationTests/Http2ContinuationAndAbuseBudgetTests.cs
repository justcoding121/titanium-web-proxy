using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Covers the three HTTP/2 abuse-resistance mechanisms from hardening plan Phase D.11: the
///     CONTINUATION frame-count/wall-clock bound on an open header block, the peer-initiated
///     incomplete-stream-reset budget (Rapid Reset / CVE-2023-44487 mitigation), and the consolidated
///     concurrent-stream cap that keeps the value the proxy advertises to the client in SETTINGS in sync
///     with the value it actually enforces. Each test drives a hand-rolled <see cref="Http2RawClient" />
///     and/or <see cref="Http2RawOriginServer" /> directly at the frame level, since none of these
///     scenarios can be triggered through a conformant HTTP/2 stack's public API.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2ContinuationAndAbuseBudgetTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private static ProxyResourceLimits CreateLimits(
        int maxOpenHeaderBlockFrames = 128,
        TimeSpan? maxOpenHeaderBlockDuration = null,
        int? maxPeerInitiatedIncompleteStreamResets = 100,
        int maxConcurrentStreamsPerConnection = 100)
    {
        return ProxyResourceLimits.Create(
            maxHeaderLineBytes: 64 * 1024,
            maxHeaderCount: 256,
            maxHeaderAggregateBytes: 256 * 1024,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: 200,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: maxConcurrentStreamsPerConnection,
            maxPeerInitiatedIncompleteStreamResets: maxPeerInitiatedIncompleteStreamResets,
            maxOpenHeaderBlockFrames: maxOpenHeaderBlockFrames,
            maxOpenHeaderBlockDuration: maxOpenHeaderBlockDuration ?? TimeSpan.FromSeconds(10),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 4,
            maxCertificateCacheEntries: null);
    }

    private static byte[] ErrorCodePayload(Http2ErrorCode code)
    {
        var payload = new byte[4];
        var value = (int)code;
        payload[0] = (byte)((value >> 24) & 0xff);
        payload[1] = (byte)((value >> 16) & 0xff);
        payload[2] = (byte)((value >> 8) & 0xff);
        payload[3] = (byte)(value & 0xff);
        return payload;
    }

    /// <summary>Reads frames until a GOAWAY arrives (or the timeout elapses), skipping everything else.</summary>
    private static async Task<Http2ErrorCode> ReadUntilGoAwayAsync(Http2RawFrame.Connection connection,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var readTask = Task.Run(async () =>
        {
            while (true)
            {
                Http2RawFrame.Frame frame;
                try
                {
                    frame = await connection.ReadFrameAsync();
                }
                catch (IOException ex) when (cts.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for a GOAWAY frame.", ex);
                }

                if (frame.Type == Http2FrameType.GoAway)
                {
                    var ec = (frame.Payload[4] << 24) | (frame.Payload[5] << 16) |
                             (frame.Payload[6] << 8) | frame.Payload[7];
                    return (Http2ErrorCode)ec;
                }
            }
        }, cts.Token);

        var completed = await Task.WhenAny(readTask, Task.Delay(timeout, CancellationToken.None));
        if (completed != readTask)
        {
            cts.Cancel();
            Assert.Fail("Timed out waiting for a GOAWAY frame.");
        }

        return await readTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CONTINUATION flood bound: a header block that never sets END_HEADERS must
    // be torn down by frame count, since zero-length CONTINUATION frames never
    // trip the pre-existing byte cap.
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [Timeout(15_000)]
    public async Task Client_ContinuationFlood_ExceedingFrameCount_ReceivesGoAwayWithEnhanceYourCalm()
    {
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ResourceLimits = CreateLimits(maxOpenHeaderBlockFrames: 5);

        var serverUri = new Uri(sharedServer.ListeningHttpsUrl);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        // Start reading before the flood so GOAWAY is observed even if the proxy tears the TCP
        // connection down immediately afterward (common on busy CI runners).
        var goAwayTask = ReadUntilGoAwayAsync(rawClient.Connection, TimeSpan.FromSeconds(10));

        // Open a header block on stream 1 without END_HEADERS, so the proxy starts buffering and
        // counting CONTINUATION frames for it.
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, 0, Array.Empty<byte>());

        // Send more zero-length CONTINUATION frames than the configured cap tolerates. Zero-length
        // frames never advance the byte-based cap, so only the frame-count bound can catch this.
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.Continuation, 1, 0, Array.Empty<byte>());
            }
            catch (IOException)
            {
                // Proxy may close the socket as soon as the CONTINUATION budget is breached.
                break;
            }
        }

        var errorCode = await goAwayTask;

        Assert.AreEqual(Http2ErrorCode.EnhanceYourCalm, errorCode,
            "An open header block that exceeds the configured CONTINUATION frame-count bound must be " +
            "torn down with GOAWAY(ENHANCE_YOUR_CALM).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rapid Reset (CVE-2023-44487) abuse budget: repeated client-initiated resets
    // of streams that never completed must eventually tear the connection down
    // and refuse further new streams, rather than let the client churn forever.
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [Timeout(20_000)]
    public async Task Client_RepeatedIncompleteStreamResets_ExceedingBudget_ReceivesGoAwayAndFurtherStreamsAreRefused()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var rawServer = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            // Never answer any request, so every stream the client resets is still "incomplete"
            // (never reached a normal end-stream) from the proxy's point of view.
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ResourceLimits = CreateLimits(maxPeerInitiatedIncompleteStreamResets: 3);

        var serverUri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        async Task OpenAndResetAsync(int streamId)
        {
            var headers = rawClient.Connection.EncodeHeaders(
                new[]
                {
                    (":method", "GET"), (":scheme", "https"),
                    (":authority", $"{serverUri.Host}:{serverUri.Port}"), (":path", "/")
                },
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(streamId, headers, true);
            await rawClient.Connection.WriteFrameAsync(Http2FrameType.RstStream, streamId, 0,
                ErrorCodePayload(Http2ErrorCode.Cancel));
        }

        // Exceed the configured budget of 3 peer-initiated incomplete-stream resets.
        for (var i = 0; i < 6; i++)
        {
            await OpenAndResetAsync(2 * i + 1);
        }

        var errorCode = await ReadUntilGoAwayAsync(rawClient.Connection, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2ErrorCode.EnhanceYourCalm, errorCode,
            "Exceeding the peer-initiated incomplete-stream-reset budget must tear the connection down " +
            "with GOAWAY(ENHANCE_YOUR_CALM).");

        // A new stream opened above the announced last-stream-id must be refused locally rather than
        // forwarded, since the proxy already told the client (via the GOAWAY above) it will not admit
        // any further client-initiated streams past that point.
        var newStreamId = 25;
        var newHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"),
                (":authority", $"{serverUri.Host}:{serverUri.Port}"), (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(newStreamId, newHeaders, true);

        var (headersResult, _, refusalCode) = await rawClient.Connection.ReadHeadersOrRstAsync(newStreamId);
        Assert.IsNull(headersResult, "A stream opened after the reset budget was exceeded must not be admitted.");
        Assert.AreEqual(Http2ErrorCode.RefusedStream, refusalCode);

        cts.Cancel();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Consolidated concurrency cap: the SETTINGS_MAX_CONCURRENT_STREAMS value
    // relayed to the client must be clamped to the proxy-owned cap, never the
    // origin's own (possibly much larger) advertised value - otherwise the
    // client believes it has more budget than the proxy will actually enforce
    // (RFC 9113 §5.1.2's PROTOCOL_ERROR vs. REFUSED_STREAM ambiguity).
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_AdvertisingLargerMaxConcurrentStreams_IsClampedToProxyOwnedCapBeforeRelayToClient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var rawServer = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawServer.HandleConnection(async connection =>
        {
            // Advertise a much larger concurrency budget than the proxy is configured to enforce.
            var payload = new byte[6];
            var id = (int)Http2SettingsId.MaxConcurrentStreams;
            const int advertised = 1000;
            payload[0] = (byte)((id >> 8) & 0xff);
            payload[1] = (byte)(id & 0xff);
            payload[2] = (byte)((advertised >> 24) & 0xff);
            payload[3] = (byte)((advertised >> 16) & 0xff);
            payload[4] = (byte)((advertised >> 8) & 0xff);
            payload[5] = (byte)(advertised & 0xff);
            await connection.WriteFrameAsync(Http2FrameType.Settings, 0, 0, payload);

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ResourceLimits = CreateLimits(maxConcurrentStreamsPerConnection: 5);

        var serverUri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        var relayedSettings = await rawClient.Connection.ReadSettingsAsync();
        cts.Cancel();

        Assert.IsTrue(relayedSettings.ContainsKey((int)Http2SettingsId.MaxConcurrentStreams),
            "The relayed SETTINGS frame must carry an explicit SETTINGS_MAX_CONCURRENT_STREAMS entry.");
        Assert.AreEqual(5, relayedSettings[(int)Http2SettingsId.MaxConcurrentStreams],
            "The value relayed to the client must be clamped to the proxy-owned cap, not the origin's " +
            "own (larger) advertised value.");
    }
}
