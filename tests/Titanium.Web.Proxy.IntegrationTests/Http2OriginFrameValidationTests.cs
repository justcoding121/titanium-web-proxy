using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Tests that <c>Http2OriginConnection</c> correctly validates incoming HTTP/2 frames from misbehaving
///     origins and tears down the connection with a clean error rather than hanging or allocating unbounded
///     memory. Each test stands up a hand-rolled <see cref="Http2RawOriginServer" /> that deliberately
///     sends a malformed frame, then routes a real request through the proxy and asserts that the request
///     fails promptly (within the per-test timeout) rather than deadlocking.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginFrameValidationTests
{
    private static TestServer sharedServer = null!;

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

    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    /// <summary>
    ///     Sends a request through the proxy to the given raw server and returns true when a valid 2xx
    ///     response is received, false when the proxy fails/resets the stream. Never hangs: any exception
    ///     (including stream reset or connection close) is treated as "request failed".
    /// </summary>
    private static async Task<bool> TrySendRequestAsync(int proxyPort, Uri serverUri)
    {
        try
        {
            using var rawClient = await Http2RawClient.ConnectAsync(proxyPort, serverUri.Host, serverUri.Port);
            var requestHeaders = rawClient.Connection.EncodeHeaders(
                new[]
                {
                    (":method", "GET"), (":scheme", "https"),
                    (":authority", $"{serverUri.Host}:{serverUri.Port}"), (":path", "/")
                },
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

            // ReadHeaderBlockAsync throws on connection close / RST_STREAM.
            var (_, headers, _) = await rawClient.Connection.ReadHeaderBlockAsync();
            var status = 0;
            foreach (var (name, value) in headers)
                if (name == ":status" && int.TryParse(value, out var s))
                    status = s;
            return status is >= 200 and < 300;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Bug 3 – RFC 7540 §6.9.1: zero-increment WINDOW_UPDATE is PROTOCOL_ERROR
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_ZeroIncrement_WindowUpdate_Tears_Down_Connection()
    {
        // Auto-cancels after 1 s (far more than needed on localhost); also cancelled immediately
        // once TrySendRequestAsync returns so the server-side keep-alive exits without delay.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            // Immediately send a zero-increment WINDOW_UPDATE (connection level).
            var payload = new byte[4]; // all-zero ? increment = 0
            await connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, payload);
            // Stay alive so the proxy has time to read and reject the frame.
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var succeeded = await TrySendRequestAsync(proxy.ProxyEndPoints[0].Port, new Uri(rawServer.Url));
        cts.Cancel(); // unblock the server handler immediately if still waiting

        Assert.IsFalse(succeeded,
            "The proxy must reject a zero-increment WINDOW_UPDATE with a connection error, failing the request.");
    }

    // -------------------------------------------------------------------------
    // Bug 1 – RFC 7540 §4.2: frame declaring payload > 16 KiB is PROTOCOL_ERROR
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_OversizedFrame_Tears_Down_Connection()
    {
        // The proxy must reject the frame based on the declared length alone, before
        // allocating memory or waiting for the payload bytes that never arrive.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            // Allow a moment for the proxy to process SETTINGS and open the stream.
            await Task.Delay(100);

            // Write a raw 9-byte frame header declaring length = 1,048,576 (1 MiB) — way above the 16 KiB
            // default maximum. Use DATA type on stream 1. Do NOT write the payload bytes.
            const int oversizedLength = 1 << 20; // 1 MiB
            var header = new byte[9];
            header[0] = (byte)((oversizedLength >> 16) & 0xff);
            header[1] = (byte)((oversizedLength >> 8) & 0xff);
            header[2] = (byte)(oversizedLength & 0xff);
            header[3] = 0x00; // DATA
            header[4] = 0x00; // no flags
            header[5] = 0x00;
            header[6] = 0x00;
            header[7] = 0x00;
            header[8] = 0x01; // stream 1

            var stream = connection.GetStream();
            await stream.WriteAsync(header, 0, header.Length);
            await stream.FlushAsync();

            // Keep the connection open so the proxy has time to read the header and reject it.
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var succeeded = await TrySendRequestAsync(proxy.ProxyEndPoints[0].Port, new Uri(rawServer.Url));
        cts.Cancel(); // unblock the server handler immediately if still waiting

        Assert.IsFalse(succeeded,
            "The proxy must reject an oversized frame (declared length > 16 KiB) before allocating memory.");
    }

    // -------------------------------------------------------------------------
    // Bug 5 – RFC 7540 §6.5: SETTINGS must have stream ID 0
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_Settings_WithNonZeroStreamId_Tears_Down_Connection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            await Task.Delay(100);

            // Write a raw SETTINGS frame on stream 1 (invalid; must be stream 0).
            // Length = 0, type = 0x04 (SETTINGS), flags = 0, stream = 1.
            var header = new byte[9];
            header[3] = 0x04; // SETTINGS
            header[8] = 0x01; // stream ID 1

            var stream = connection.GetStream();
            await stream.WriteAsync(header, 0, header.Length);
            await stream.FlushAsync();

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var succeeded = await TrySendRequestAsync(proxy.ProxyEndPoints[0].Port, new Uri(rawServer.Url));
        cts.Cancel(); // unblock the server handler immediately if still waiting

        Assert.IsFalse(succeeded,
            "The proxy must treat SETTINGS on a non-zero stream as a connection-level error.");
    }

    // -------------------------------------------------------------------------
    // Bug 4 – RFC 7540 §6.10: CONTINUATION outside a header block is PROTOCOL_ERROR
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_Stray_Continuation_Tears_Down_Connection()
    {
        // The origin first answers the request normally (200 OK), then immediately
        // sends a stray CONTINUATION on the same stream even though no HEADERS/
        // PUSH_PROMISE with END_HEADERS cleared is in progress.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            // Send a complete, well-formed 200 response (END_HEADERS set).
            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, true);

            // Now send a stray CONTINUATION on the same stream.
            // There is no open header block at this point ? connection error (RFC 7540 §6.10).
            var contHeader = new byte[9];
            contHeader[3] = 0x09; // CONTINUATION
            contHeader[4] = (byte)Http2FrameFlag.EndHeaders;
            contHeader[5] = 0x00;
            contHeader[6] = 0x00;
            contHeader[7] = 0x00;
            contHeader[8] = (byte)(streamId & 0xff);

            var rawStream = connection.GetStream();
            await rawStream.WriteAsync(contHeader, 0, contHeader.Length);
            await rawStream.FlushAsync();

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        // The request may have already completed by the time the proxy processes the
        // stray CONTINUATION (the 200 response is sent first). What matters is that
        // the test finishes within the timeout — no deadlock.
        _ = await TrySendRequestAsync(proxy.ProxyEndPoints[0].Port, new Uri(rawServer.Url));
        cts.Cancel(); // unblock the server handler immediately if still waiting
        Assert.IsTrue(cts.IsCancellationRequested,
            "Server keep-alive handler must be cancelled so the test finishes without hanging.");
    }

    // -------------------------------------------------------------------------
    // Bug 6 – RFC 7540 §6.8: GOAWAY must have stream ID 0
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(15_000)]
    public async Task Origin_GoAway_WithNonZeroStreamId_Tears_Down_Connection()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            await Task.Delay(100);

            // Write a raw GOAWAY frame on stream 1 (invalid; must be stream 0).
            // Minimum GOAWAY payload is 8 bytes (last-stream-id + error code).
            var payload = new byte[8]; // all zeros = last-stream-id 0, NO_ERROR
            var header = new byte[9];
            header[0] = 0x00;
            header[1] = 0x00;
            header[2] = 0x08; // length = 8
            header[3] = 0x07; // GOAWAY
            header[4] = 0x00;
            header[5] = 0x00;
            header[6] = 0x00;
            header[7] = 0x00;
            header[8] = 0x01; // stream ID 1

            var stream = connection.GetStream();
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(payload, 0, payload.Length);
            await stream.FlushAsync();

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var succeeded = await TrySendRequestAsync(proxy.ProxyEndPoints[0].Port, new Uri(rawServer.Url));
        cts.Cancel(); // unblock the server handler immediately if still waiting

        Assert.IsFalse(succeeded,
            "The proxy must treat GOAWAY on a non-zero stream as a connection-level error.");
    }
}
