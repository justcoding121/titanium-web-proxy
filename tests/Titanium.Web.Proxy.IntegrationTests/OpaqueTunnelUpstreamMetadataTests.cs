using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     A CONNECT tunnel that is relayed opaquely (excluded host, plaintext CONNECT, or unparseable
///     method) owns its upstream connection outright, unlike a decrypted tunnel where the inner
///     requests acquire it. That connection's metadata must be reachable from the tunnel session.
/// </summary>
[DoNotParallelize]
[TestClass]
public class OpaqueTunnelUpstreamMetadataTests
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

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Opaque_Tunnel_Exposes_Upstream_Connection_Metadata()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("tunnel-ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableRequestTimingCapture = true;

        TunnelConnectSessionEventArgs? tunnelArgs = null;
        var endPoint = proxy.ProxyEndPoints.OfType<ExplicitProxyEndPoint>().First();
        endPoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            // Forces the raw byte-relay path instead of decrypt-and-inspect.
            e.DecryptSsl = false;
            tunnelArgs = e;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpsUrl);
        Assert.AreEqual("tunnel-ok", body);

        Assert.IsNotNull(tunnelArgs, "the CONNECT tunnel session should have been observed");
        Assert.AreNotEqual(0, tunnelArgs.ServerConnectionId,
            "an opaquely relayed tunnel owns a real upstream connection and must expose its id");
        Assert.IsNotNull(tunnelArgs.ServerRemoteEndPoint);
        Assert.AreEqual(server.HttpsListeningPort, tunnelArgs.ServerRemoteEndPoint.Port);
        Assert.IsNotNull(tunnelArgs.UpstreamConnectionTiming);
        Assert.AreEqual(tunnelArgs.ServerConnectionId, tunnelArgs.Timing?.UpstreamConnectionId);
        Assert.IsFalse(tunnelArgs.HttpClient.HasConnection,
            "the relay drives the socket directly, so it must not take HTTP/1.1 stream ownership");
    }
}
