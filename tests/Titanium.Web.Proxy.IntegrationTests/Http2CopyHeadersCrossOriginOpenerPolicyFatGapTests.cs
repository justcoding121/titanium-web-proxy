using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: Cross-Origin-Opener-Policy on final responses reaches the H2 client.
/// </summary>
[TestClass]
public class Http2CopyHeadersCrossOriginOpenerPolicyFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadHeaderAsync(Http2RawClient raw, string headerName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var decoded = raw.Connection.DecodeHeaders(frame.Payload);
            if (decoded.FirstOrDefault(h => h.Name == ":status").Value is not "200")
                continue;
            return decoded.FirstOrDefault(h => h.Name == headerName).Value;
        }

        return null;
    }

    /// <summary>
    ///     Bug class: COOP from the origin is stripped on the H2 MITM relay.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_CoopSameOrigin_On200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("cross-origin-opener-policy", "same-origin"),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: true);
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/coop")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var coop = await ReadHeaderAsync(raw, "cross-origin-opener-policy");
        Assert.IsNotNull(coop);
        Assert.AreEqual("same-origin", coop);
    }
}
