using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ConnectionPoolTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Connection_Pool_Is_Enabled_By_Default_And_Reuses_Server_Connection()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();

        // Kestrel assigns a distinct Connection.Id per upstream TCP connection, so reuse of a pooled
        // proxy -> server connection shows up as the same id across sequential requests.
        var connectionIds = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            connectionIds.Add(context.Connection.Id);
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        Assert.IsTrue(proxy.EnableConnectionPool, "connection pool should be enabled by default");

        using var client = testSuite.GetClient(proxy);

        // sequential requests over the same client connection: the proxy should reuse one upstream connection
        for (var i = 0; i < 4; i++)
        {
            var body = await client.GetStringAsync(server.ListeningHttpUrl);
            Assert.AreEqual("ok", body);
        }

        Assert.AreEqual(1, connectionIds.Distinct().Count(),
            "the proxy should have reused a single pooled upstream connection across the requests");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Connection_Pool_Disabled_Does_Not_Reuse_Across_Client_Connections()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();

        var connectionIds = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            connectionIds.Add(context.Connection.Id);
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableConnectionPool = false;

        // Two separate client connections. Pooling governs reuse ACROSS client connections, so with it
        // disabled each client connection must open its own upstream connection.
        // (Within a single client connection the upstream connection is reused regardless of pooling.)
        using (var client1 = testSuite.GetClient(proxy))
            Assert.AreEqual("ok", await client1.GetStringAsync(server.ListeningHttpUrl));

        using (var client2 = testSuite.GetClient(proxy))
            Assert.AreEqual("ok", await client2.GetStringAsync(server.ListeningHttpUrl));

        Assert.AreEqual(2, connectionIds.Distinct().Count(),
            "without pooling each client connection should get its own upstream connection");
    }
}
