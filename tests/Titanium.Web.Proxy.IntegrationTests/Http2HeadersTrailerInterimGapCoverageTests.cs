using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining Copy.Headers / bridge gaps: forbidden request trailers beyond content-length,
///     native H2 interim 103 Early Hints, and BeforeRequest header strip + re-encode.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2HeadersTrailerInterimGapCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down */ }
    };

    private static async Task AssertTrailerForbiddenFieldRejectedAsync(string fieldName, string fieldValue)
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var reqBlock = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/trailers")],
            [("content-length", "2")]);
        await raw.Connection.WriteHeaderBlockAsync(1, reqBlock, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, (Http2FrameFlag)0,
            Encoding.ASCII.GetBytes("ab"));

        var trailerBlock = raw.Connection.EncodeHeaders(
            Array.Empty<(string, string)>(),
            [(fieldName, fieldValue)]);
        await raw.Connection.WriteHeaderBlockAsync(1, trailerBlock, endStream: true);

        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal == null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                terminal = frame;
        }

        Assert.IsNotNull(terminal, $"Trailer field '{fieldName}' must RST/GOAWAY.");
        if (terminal.Value.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, terminal.Value.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(terminal.Value.Payload));
        }
    }

    /// <summary>
    ///     RFC 9110 §6.5.1: TE is forbidden in trailers.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Client_RequestTrailer_With_Te_Forbidden_TriggersRst() =>
        AssertTrailerForbiddenFieldRejectedAsync("te", "trailers");

    /// <summary>
    ///     RFC 9110 §6.5.1: Host is forbidden in trailers.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Client_RequestTrailer_With_Host_Forbidden_TriggersRst() =>
        AssertTrailerForbiddenFieldRejectedAsync("host", "evil.example");

    /// <summary>
    ///     RFC 9110 §6.5.1: Transfer-Encoding is forbidden in trailers.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task Client_RequestTrailer_With_TransferEncoding_Forbidden_TriggersRst() =>
        AssertTrailerForbiddenFieldRejectedAsync("transfer-encoding", "chunked");

    /// <summary>
    ///     Bug class: native H2 MITM drops 103 Early Hints and only forwards the final 200,
    ///     breaking Early Hints preload clients.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2_Origin_Interim103_Then200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var interim = conn.EncodeHeaders([(":status", "103")],
                [("link", "</style.css>; rel=preload; as=style")]);
            await conn.WriteHeaderBlockAsync(streamId, interim, endStream: false);
            var final = conn.EncodeHeaders([(":status", "200")],
                [("content-type", "text/plain"), ("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/early")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        string? firstStatus = null;
        string? finalStatus = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && finalStatus is null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type != Http2FrameType.Headers && frame.Type != Http2FrameType.Continuation)
                continue;
            var headers = raw.Connection.DecodeHeaders(frame.Payload);
            var status = headers.FirstOrDefault(h => h.Name == ":status").Value;
            if (string.IsNullOrEmpty(status))
                continue;
            if (firstStatus is null)
                firstStatus = status;
            else
                finalStatus = status;
        }

        Assert.AreEqual("103", firstStatus, "Proxy must relay 103 Early Hints before the final response.");
        Assert.AreEqual("200", finalStatus, "Final 200 must follow the interim 103.");
    }

    /// <summary>
    ///     Bug class: BeforeRequest header removal is ignored because MITM still relays the
    ///     original compressed HEADERS block.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequest_RemovedHeader_DoesNotReachOrigin()
    {
        string? seenSecret = "unset";
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            seenSecret = headers.FirstOrDefault(h => h.Name == "x-secret").Value;
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.RemoveHeader("x-secret");
            return Task.CompletedTask;
        };

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/strip")],
            [("x-secret", "should-not-leak")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && seenSecret == "unset")
            await Task.Delay(20);

        Assert.IsTrue(string.IsNullOrEmpty(seenSecret),
            $"Removed header must not reach origin; saw '{seenSecret}'.");
    }

    /// <summary>
    ///     Bug class: H2→H1 BeforeRequest Ok still opens an origin TCP connection.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeRequest_Ok_DoesNotContactOrigin()
    {
        var originContacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            originContacted = true;
            return ctx.Response.WriteAsync("should-not-reach");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("bridged-ok");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync($"https://localhost:{server.HttpsListeningPort}/synth");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("bridged-ok", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(originContacted, "Synthetic Ok on H2→H1 must not contact the origin.");
    }
}
