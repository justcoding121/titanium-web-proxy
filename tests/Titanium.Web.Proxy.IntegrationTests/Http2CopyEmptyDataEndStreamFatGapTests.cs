using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.cs</c> suite: zero-length DATA with END_STREAM after response headers
///     still completes the client stream successfully.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyEmptyDataEndStreamFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                var decoded = raw.Connection.DecodeHeaders(frame.Payload);
                return decoded.FirstOrDefault(h => h.Name == ":status").Value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Bug class: origin DATA frame of length 0 with END_STREAM wedges the H2 client stream.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_EmptyDataEndStream_ClientSees200()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var hdr = conn.EncodeHeaders([(":status", "200")], []);
                await conn.WriteHeaderBlockAsync(streamId, hdr, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, []);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/empty-data")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var status = await ReadStatusAsync(raw);
        Assert.AreEqual("200", status);
    }
}
