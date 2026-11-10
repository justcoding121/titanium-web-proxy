using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: origin RST_STREAM with NO_ERROR / CANCEL /
///     STREAM_CLOSED after a completed response must be absorbed so the client still
///     receives the body (HttpClient otherwise fails mid-CopyTo).
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginBenignRstFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task AssertBenignOriginRstStillDeliversBodyAsync(Http2ErrorCode error)
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("benign-rst-body");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);

                var rst = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(rst, (int)error);
                await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, (Http2FrameFlag)0, rst);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        using var response = await client.GetAsync(origin.Url + "rst");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"Origin RST {error} after END_STREAM must not fail the client response.");
        Assert.AreEqual("benign-rst-body", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: origin RST_STREAM(NO_ERROR) after a finished response is relayed to the
    ///     client and HttpClient fails despite a complete body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task OriginRst_NoError_AfterCompleteResponse_ClientStillGetsBody() =>
        AssertBenignOriginRstStillDeliversBodyAsync(Http2ErrorCode.NoError);

    /// <summary>
    ///     Bug class: origin RST_STREAM(CANCEL) after END_STREAM is treated as a hard failure.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task OriginRst_Cancel_AfterCompleteResponse_ClientStillGetsBody() =>
        AssertBenignOriginRstStillDeliversBodyAsync(Http2ErrorCode.Cancel);

    /// <summary>
    ///     Bug class: origin RST_STREAM(STREAM_CLOSED) after END_STREAM is treated as a hard failure.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public Task OriginRst_StreamClosed_AfterCompleteResponse_ClientStillGetsBody() =>
        AssertBenignOriginRstStillDeliversBodyAsync(Http2ErrorCode.StreamClosed);

    /// <summary>
    ///     Bug class: HEADERS-only 204 followed by origin RST(NO_ERROR) fails the next stream
    ///     on the same connection.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task OriginRst_NoError_After204_NextStreamStillWorks()
    {
        var hits = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                System.Threading.Interlocked.Increment(ref hits);
                var path = headers.First(h => h.Name == ":path").Value;
                if (path.Contains("first", StringComparison.Ordinal))
                {
                    var resp = conn.EncodeHeaders([(":status", "204")], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
                    var rst = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.NoError);
                    await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, (Http2FrameFlag)0, rst);
                }
                else
                {
                    var payload = Encoding.UTF8.GetBytes("second");
                    var resp = conn.EncodeHeaders(
                        [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        using (var first = await client.GetAsync(origin.Url + "first"))
            Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);
        using var second = await client.GetAsync(origin.Url + "second");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("second", await second.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits);
    }
}
