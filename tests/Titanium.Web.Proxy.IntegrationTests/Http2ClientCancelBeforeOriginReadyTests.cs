using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Chrome opens a connection, sends a GET on stream 1, cancels it with RST_STREAM and immediately
///     re-sends the same GET on stream 3 (iframe / script re-issue). When this happens before the origin leg
///     has finished its handshake the proxy used to answer with GOAWAY(PROTOCOL_ERROR), which the browser
///     showed as <c>ERR_HTTP2_PROTOCOL_ERROR</c>.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2ClientCancelBeforeOriginReadyTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public Task ControlTwoStreamsWithoutCancel_AreBothAnswered() => RunAsync(cancelFirst: false);

    [TestMethod]
    [Timeout(60 * 1000)]
    public Task RstStreamThenNewStream_BeforeOriginReady_IsAnsweredWithoutGoAway() => RunAsync(cancelFirst: true);

    private static async Task RunAsync(bool cancelFirst)
    {
        var idleResets = new ConcurrentQueue<int>();
        var opened = new ConcurrentDictionary<int, bool>();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate,
            TimeSpan.FromMilliseconds(400));
        origin.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var frame = await connection.ReadFrameAsync();

                // RFC 9113 section 6.4: RST_STREAM on an idle stream is a connection error (PROTOCOL_ERROR);
                // a real origin answers it with GOAWAY, which the proxy relays to the browser.
                if (frame.Type == Http2FrameType.RstStream && !opened.ContainsKey(frame.StreamId))
                    idleResets.Enqueue(frame.StreamId);

                if (frame.Type != Http2FrameType.Headers)
                    continue;

                opened[frame.StreamId] = true;

                var block = frame.Payload;
                if ((frame.Flags & Http2FrameFlag.Priority) != 0)
                    block = block.AsSpan(5).ToArray();
                connection.DecodeHeaders(block);

                var response = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(frame.StreamId, response, endStream: true);
            }
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += async (_, _) => await Task.Delay(150);
        proxy.BeforeResponse += (_, _) => Task.CompletedTask;

        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);
        byte[] Block() => raw.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{origin.Port}"),
                (":path", "/iframe")
            },
            new[] { ("accept", "text/html") });

        await WriteChromeStyleHeadersAsync(raw, 1, Block());
        if (cancelFirst)
        {
            var cancel = new byte[4];
            cancel[3] = (byte)Http2ErrorCode.Cancel;
            await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, cancel);
        }

        await WriteChromeStyleHeadersAsync(raw, 3, Block());

        var (headers, _, errorCode) = await raw.Connection.ReadHeadersOrRstAsync(3).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsNotNull(headers, $"Expected a response on stream 3 but got {errorCode}.");
        Assert.IsTrue(headers.Any(h => h.Name == ":status" && h.Value == "200"));
        Assert.IsTrue(idleResets.IsEmpty,
            $"The origin received RST_STREAM for idle stream(s) {string.Join(",", idleResets)}: the proxy relayed a cancel for a request it never forwarded.");
    }

    // Chrome sets the PRIORITY flag on every request HEADERS: exclusive dependency on stream 0, weight 256.
    private static Task WriteChromeStyleHeadersAsync(Http2RawClient raw, int streamId, byte[] block)
    {
        var payload = new byte[5 + block.Length];
        payload[0] = 0x80;
        payload[4] = 255;
        block.CopyTo(payload, 5);
        return raw.Connection.WriteFrameAsync(Http2FrameType.Headers, streamId,
            Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream | Http2FrameFlag.Priority, payload);
    }
}
