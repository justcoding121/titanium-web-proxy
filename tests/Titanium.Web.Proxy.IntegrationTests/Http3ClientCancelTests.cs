#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     HTTP/3 client cancel and origin abort paths.
///     Bug class: cancelled upload leaves origin stream or connection leaked.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3ClientCancelTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClientCancel_MidUpload_DoesNotHangProxy_AndLaterRequestWorks()
    {
        // Bug class: cancelled H3 POST leaves the proxy accept loop stuck.
        RequireQuic();

        var sawBody = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(async req =>
        {
            sawBody.TrySetResult(req.Body.Length);
            return new QuicHttp3Response(200, "ok");
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            try
            {
                using var content = new ByteArrayContent(new byte[4 * 1024 * 1024]);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                await client.PostAsync($"https://localhost:{origin.Port}/big", content, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: client cancelled mid-upload.
            }
        }

        // A later request must still succeed on a healthy proxy.
        using var response = await client.GetAsync($"https://localhost:{origin.Port}/after");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("ok", body);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task OriginReset_AfterPartialResponse_ReturnsErrorNotHang()
    {
        // Bug class: origin abort after headers leaves the H1 client hanging.
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => throw new InvalidOperationException("origin-abort"));

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync($"https://localhost:{origin.Port}/boom");
        Assert.IsTrue(
            (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.BadGateway,
            $"expected upstream failure status, got {(int)response.StatusCode}");
    }
}
