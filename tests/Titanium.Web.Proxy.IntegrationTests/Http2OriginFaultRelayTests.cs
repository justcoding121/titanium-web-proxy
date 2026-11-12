using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Origin-side HTTP/2 faults through the MITM relay.
///     Bug class: RST/GOAWAY/flow-control stalls hang the client or leak connections.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginFaultRelayTests
{
    [TestMethod]
    [Timeout(45_000)]
    public async Task OriginRstStream_BeforeHeaders_ReturnsErrorToClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            await conn.WriteFrameAsync(
                Http2FrameType.RstStream, streamId, (Http2FrameFlag)0,
                BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)Http2ErrorCode.InternalError)));
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var sawTerminal = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !sawTerminal)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
            {
                sawTerminal = true;
            }
            else if (frame.Type == Http2FrameType.Headers)
            {
                var status = raw.Connection.DecodeHeaders(frame.Payload).FirstOrDefault(h => h.Name == ":status").Value;
                if (status is not null && status.StartsWith('5'))
                {
                    sawTerminal = true;
                }
            }
        }

        Assert.IsTrue(sawTerminal, "client must see RST/GOAWAY or a 5xx after origin RST before headers");
    }

    [TestMethod]
    [Timeout(45_000)]
    public async Task OriginGoAway_WithInFlightRequest_DoesNotHangNextRequest()
    {
        var requestCount = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var n = System.Threading.Interlocked.Increment(ref requestCount);
                if (n == 1)
                {
                    var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "1")]);
                    await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "a"u8.ToArray());
                    // GOAWAY after answering the first request.
                    var payload = new byte[8];
                    BitConverter.TryWriteBytes(payload.AsSpan(0, 4), IPAddress.HostToNetworkOrder(streamId));
                    BitConverter.TryWriteBytes(payload.AsSpan(4, 4), IPAddress.HostToNetworkOrder((int)Http2ErrorCode.NoError));
                    await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, payload);
                    return;
                }

                var ok = conn.EncodeHeaders([(":status", "200")], [("content-length", "1")]);
                await conn.WriteHeaderBlockAsync(streamId, ok, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "b"u8.ToArray());
            }
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var first = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("b", await second.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(45_000)]
    public async Task OriginWindowUpdateStall_ThenRelease_CompletesLargeBody()
    {
        // Bug class: flow-control stall never resumes after WINDOW_UPDATE.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var payload = Encoding.ASCII.GetBytes(new string('x', 64 * 1024));
            var headers = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-length", payload.Length.ToString())]);
            await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            // Chunk under the default max frame size; a healthy proxy must keep WINDOW_UPDATEing.
            const int chunk = 8 * 1024;
            for (var offset = 0; offset < payload.Length; offset += chunk)
            {
                var length = Math.Min(chunk, payload.Length - offset);
                var last = offset + length >= payload.Length;
                await conn.WriteFrameAsync(
                    Http2FrameType.Data, streamId,
                    last ? Http2FrameFlag.EndStream : (Http2FrameFlag)0,
                    payload.AsMemory(offset, length).ToArray());
                if (offset == 0)
                {
                    await Task.Delay(50);
                }
            }
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.GetAsync(origin.Url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(64 * 1024, body.Length);
    }
}
