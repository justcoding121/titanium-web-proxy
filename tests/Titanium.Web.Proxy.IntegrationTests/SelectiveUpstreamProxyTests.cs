using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #753: selective CustomUpStreamProxy — some requests nested CONNECT
///     through an upstream proxy, others go direct.
/// </summary>
[TestClass]
public class SelectiveUpstreamProxyTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task CustomUpStreamProxy_OnlySelectedHttpsRequests_UseNestedConnect()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            var marker = context.Request.Path.Value?.Contains("via-upstream") == true
                ? "via-upstream"
                : "direct";
            return context.Response.WriteAsync(marker);
        });

        var upstream = testSuite.GetProxy();
        upstream.ViaHeaderPseudonym = "upstream-proxy";
        var upstreamConnectCount = 0;
        var upstreamEp = upstream.ProxyEndPoints.OfType<ExplicitProxyEndPoint>().First();
        upstreamEp.BeforeTunnelConnectRequest += (_, _) =>
        {
            Interlocked.Increment(ref upstreamConnectCount);
            return Task.CompletedTask;
        };

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            if (e.HttpClient.Request.RequestUri.AbsolutePath.Contains("via-upstream", StringComparison.Ordinal))
            {
                e.CustomUpStreamProxy = new ExternalProxy("localhost", upstream.ProxyEndPoints[0].Port);
            }

            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);

        var viaUpstream = await client.GetStringAsync(server.ListeningHttpsUrl.TrimEnd('/') + "/via-upstream");
        Assert.AreEqual("via-upstream", viaUpstream);

        var direct = await client.GetStringAsync(server.ListeningHttpsUrl.TrimEnd('/') + "/direct");
        Assert.AreEqual("direct", direct);

        Assert.IsTrue(upstreamConnectCount >= 1,
            "Selected HTTPS request must have issued CONNECT to the custom upstream.");
    }
}
