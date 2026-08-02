#pragma warning disable CA1416
#pragma warning disable TWP001

using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Sanity-check the H3 test origin without the proxy in the path.
/// </summary>
[TestClass]
public class Http3OriginDirectTests
{
    [TestMethod]
    public async Task Client_To_Origin_DirectRoundTrip()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic not supported.");

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req => Task.FromResult(new QuicHttp3Response(200, "direct-" + req.Path)));

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, origin.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/x");
        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("direct-/x", response.TextBody);
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
