using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: <see cref="ProxyServer.MaxCachedConnections" /> eviction
///     must not wedge pooled upstream reuse across many destinations.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryPoolCapEvictFatGapTests
{
    /// <summary>
    ///     Bug class: tightening <see cref="ProxyServer.MaxCachedConnections" /> leaks handles or wedges
    ///     subsequent upstream dials.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task MaxCachedConnections_One_ManyHosts_AllRequestsSucceed()
    {
        const int hostCount = 4;
        var servers = new List<TestServer>();
        try
        {
            for (var i = 0; i < hostCount; i++)
            {
                var srv = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
                var id = i;
                srv.HandleRequest(ctx => ctx.Response.WriteAsync("h" + id));
                servers.Add(srv);
            }

            using var suite = new TestSuite(servers[0]);
            var proxy = suite.GetProxy();
            proxy.EnableConnectionPool = true;
            proxy.MaxCachedConnections = 1;

            var tasks = new List<Task>();
            for (var i = 0; i < servers.Count; i++)
            {
                var expected = "h" + i;
                var url = servers[i].ListeningHttpUrl;
                var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
                tasks.Add(AssertBodyAsync(client, url, expected));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var srv in servers) srv.Dispose();
        }
    }

    private static async Task AssertBodyAsync(HttpClient client, string url, string expected)
    {
        try
        {
            Assert.AreEqual(expected, await client.GetStringAsync(url));
        }
        finally
        {
            client.Dispose();
        }
    }
}
