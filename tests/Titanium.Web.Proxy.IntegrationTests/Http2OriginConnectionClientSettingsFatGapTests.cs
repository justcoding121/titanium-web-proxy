using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: the bridge answers origin SETTINGS with SETTINGS ACK
///     before opening request streams.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionClientSettingsFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin SETTINGS frame never receives ACK, stalling stream open.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginSettings_IsAckedBeforeRequest()
    {
        var settingsAcked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var frame = await conn.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
                if (frame.Type == Http2FrameType.Settings && (frame.Flags & Http2FrameFlag.Ack) != 0)
                {
                    settingsAcked.TrySetResult();
                    break;
                }
            }

            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url);
        await settingsAcked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(settingsAcked.Task.IsCompletedSuccessfully);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
}
