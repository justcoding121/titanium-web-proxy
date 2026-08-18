using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

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

        // ASP.NET Core assigns a distinct Connection.Id per upstream TCP connection, so reuse of a pooled
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

    /// <summary>
    ///     Regression test for the pool-lock sharding change in <c>TcpConnectionFactory</c>: releasing
    ///     connections for many distinct destinations concurrently must not deadlock, throw, or corrupt
    ///     the per-destination queues, even though every release contends on a single
    ///     <c>ReaderWriterLockSlim</c> read lock before reaching its own destination-scoped
    ///     <c>lock (queue)</c>.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task Concurrent_Releases_Across_Many_Destinations_Complete_Without_Deadlock_Or_Loss()
    {
        const int destinationCount = 6;
        const int clientsPerDestination = 5;
        const int requestsPerClient = 4;

        var servers = new List<TestServer>();
        try
        {
            for (var i = 0; i < destinationCount; i++)
                servers.Add(new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false));

            foreach (var srv in servers)
            {
                var connectionIds = new ConcurrentBag<string>();
                srv.HandleRequest(context =>
                {
                    connectionIds.Add(context.Connection.Id);
                    return context.Response.WriteAsync("ok");
                });
            }

            using var testSuiteHost = new TestSuite(servers[0]);
            var proxy = testSuiteHost.GetProxy();
            proxy.EnableConnectionPool = true;
            proxy.MaxCachedConnections = 2;

            var tasks = new List<Task>();
            foreach (var srv in servers)
                for (var c = 0; c < clientsPerDestination; c++)
                {
                    var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
                    tasks.Add(RunSequentialRequests(client, srv.ListeningHttpUrl, requestsPerClient));
                }

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var srv in servers) srv.Dispose();
        }
    }

    private static async Task RunSequentialRequests(HttpClient client, string url, int count)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                var body = await client.GetStringAsync(url);
                Assert.AreEqual("ok", body);
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    ///     Regression test for the admission gate in <c>ProxyServer.OnAcceptConnection</c>: once
    ///     <c>MaxConcurrentClientConnections</c> admitted connections are outstanding, a new connection
    ///     must be rejected immediately (closed before any HTTP is even read), and releasing a held slot
    ///     must free capacity right away - unlike <c>ClientConnectionCount</c>, which lags behind by the
    ///     hardcoded one-second TIME_WAIT delay in <c>TcpClientConnection.Dispose</c>.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Global_Admission_Gate_Rejects_Beyond_Limit_And_Frees_Capacity_Promptly_On_Release()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        var release = new TaskCompletionSource();
        server.HandleRequest(async context =>
        {
            await release.Task;
            await context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxConcurrentClientConnections = 1;

        // Not `using`: the admission slot is held for the client's whole TCP connection (HandleClient's
        // keep-alive loop), not just one request/response, so the client must be explicitly disposed
        // (closing its pooled connection) before the slot is expected to free up.
        var heldClient = testSuite.GetClient(proxy);
        var heldRequest = heldClient.GetStringAsync(server.ListeningHttpUrl);

        // Wait for the held request's connection to actually be admitted before probing the gate,
        // rather than relying on a fixed delay.
        var admitted = await WaitForConditionAsync(() => proxy.AdmittedClientConnectionCount >= 1);
        Assert.IsTrue(admitted, "the held request should have been admitted");

        using (var rejectedClient = testSuite.GetClient(proxy))
        {
            await Assert.ThrowsExactlyAsync<HttpRequestException>(
                () => rejectedClient.GetStringAsync(server.ListeningHttpUrl),
                "a connection beyond the admission limit should be rejected before any HTTP response is produced");
        }

        Assert.IsTrue(proxy.GlobalAdmissionRejectionCount >= 1,
            "the rejection should have been counted");

        // Releasing the held connection should free the slot immediately, not after the TIME_WAIT delay
        // that ClientConnectionCount is subject to.
        release.SetResult();
        Assert.AreEqual("ok", await heldRequest);
        heldClient.Dispose();

        var freed = await WaitForConditionAsync(() => proxy.AdmittedClientConnectionCount == 0);
        Assert.IsTrue(freed, "the admission slot should be released promptly after the handler completes");

        using var followUpClient = testSuite.GetClient(proxy);
        Assert.AreEqual("ok", await followUpClient.GetStringAsync(server.ListeningHttpUrl));
    }

    /// <summary>
    ///     Regression test for the per-endpoint layer of the admission gate: <c>ProxyEndPoint.MaxConcurrentClients</c>
    ///     rejects independently of, and in addition to, the global cap.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Endpoint_Admission_Gate_Rejects_Beyond_Its_Own_Limit()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        var release = new TaskCompletionSource();
        server.HandleRequest(async context =>
        {
            await release.Task;
            await context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.ProxyEndPoints[0].MaxConcurrentClients = 1;

        using var heldClient = testSuite.GetClient(proxy);
        var heldRequest = heldClient.GetStringAsync(server.ListeningHttpUrl);

        var admitted = await WaitForConditionAsync(() => proxy.ProxyEndPoints[0].AdmittedClientCount >= 1);
        Assert.IsTrue(admitted, "the held request should have been admitted on the endpoint");

        using (var rejectedClient = testSuite.GetClient(proxy))
        {
            await Assert.ThrowsExactlyAsync<HttpRequestException>(
                () => rejectedClient.GetStringAsync(server.ListeningHttpUrl),
                "a connection beyond the endpoint's admission limit should be rejected");
        }

        Assert.IsTrue(proxy.EndpointAdmissionRejectionCount >= 1,
            "the endpoint-level rejection should have been counted");

        release.SetResult();
        Assert.AreEqual("ok", await heldRequest);
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }
}
