using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class NestedProxyTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    public async Task Smoke_Test_Nested_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy1 = testSuite.GetProxy();
        proxy1.ViaHeaderPseudonym = "proxy1";
        var proxy2 = testSuite.GetProxy(proxy1);
        proxy2.ViaHeaderPseudonym = "proxy2";

        var client = testSuite.GetClient(proxy2);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Nested_Proxy_UserData()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy1 = testSuite.GetProxy();
        proxy1.ViaHeaderPseudonym = "proxy1";
        proxy1.ProxyBasicAuthenticateFunc = async (session, username, password) =>
        {
            session!.UserData = "Test";
            return await Task.FromResult(true);
        };

        var proxy2 = testSuite.GetProxy();
        proxy2.ViaHeaderPseudonym = "proxy2";

        proxy1.GetCustomUpStreamProxyFunc = async session =>
        {
            Assert.AreEqual("Test", session.UserData!);

            return await Task.FromResult(new ExternalProxy("localhost", proxy2.ProxyEndPoints[0].Port));
        };

        var client = testSuite.GetClient(proxy1, true);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Upstream_Proxy_Failure_Fails_Over_To_New_Proxy()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("failover ok"));

        // a working upstream proxy the failover callback will switch to
        var workingUpstream = testSuite.GetProxy();
        workingUpstream.ViaHeaderPseudonym = "working-upstream";

        var proxy = testSuite.GetProxy();
        var failoverInvoked = false;

        // initial upstream points at a closed port so the first connection attempt fails
        proxy.GetCustomUpStreamProxyFunc = _ =>
            Task.FromResult<IExternalProxy?>(new ExternalProxy("localhost", 1) { ProxyType = ExternalProxyType.Http });

        proxy.CustomUpStreamProxyFailureFunc = _ =>
        {
            failoverInvoked = true;
            return Task.FromResult<IExternalProxy?>(
                new ExternalProxy("localhost", workingUpstream.ProxyEndPoints[0].Port)
                    { ProxyType = ExternalProxyType.Http });
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello"));

        Assert.IsTrue(failoverInvoked, "the failover callback should have been invoked");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("failover ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    [TestCategory("Slow")]
    public async Task Nested_Proxy_Farm_Without_Connection_Cache_Should_Not_Hang()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxies2 = new List<ProxyServer>();

        //create a level 2 upstream proxy farm that forwards to server
        for (var i = 0; i < 10; i++)
        {
            var proxy2 = testSuite.GetProxy();
            proxy2.ProxyBasicAuthenticateFunc += (_, _, _) =>
            {
                return Task.FromResult(true);
            };

            proxies2.Add(proxy2);
        }

        var proxies1 = new List<ProxyServer>();

        //create a level 1 upstream proxy farm that forwards to level 2 farm
        for (var i = 0; i < 10; i++)
        {
            var proxy1 = testSuite.GetProxy();
            proxy1.EnableConnectionPool = false;
            var proxy2 = proxies2[Random.Shared.Next() % proxies2.Count];

            proxy1.GetCustomUpStreamProxyFunc += async _ =>
            {
                var proxy = new ExternalProxy
                {
                    HostName = "localhost",
                    Port = proxy2.ProxyEndPoints[0].Port,
                    ProxyType = ExternalProxyType.Http,
                    UserName = "test_user",
                    Password = "test_password"
                };

                return await Task.FromResult(proxy);
            };

            proxies1.Add(proxy1);
        }

        var tasks = new List<Task>();

        //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
        for (var j = 0; j < 1_000; j++)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var proxy = proxies1[Random.Shared.Next() % proxies1.Count];
                    using var client = testSuite.GetClient(proxy);

                    //tests should not keep hanging indefinitely.
                    client.Timeout = TimeSpan.FromSeconds(60);
                    await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                        new StringContent("hello server. I am a client."));
                }
                //if error is thrown because of server getting overloaded its okay.
                //But client.PostAsync should'nt hang in all cases.
                catch { }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        Assert.AreEqual(1_000, tasks.Count);
        Assert.IsTrue(tasks.TrueForAll(t => t.IsCompleted),
            "Every request task must finish without hanging.");
    }


    //Reproduce bug reported so that we can fix it.
    //https://github.com/justcoding121/titanium-web-proxy/issues/826
    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    [TestCategory("Slow")]
    public async Task Nested_Proxy_Farm_With_Connection_Cache_Should_Not_Hang()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxies2 = new List<ProxyServer>();

        //create a level 2 upstream proxy farm that forwards to server
        for (var i = 0; i < 10; i++)
        {
            var proxy2 = testSuite.GetProxy();
            proxy2.ProxyBasicAuthenticateFunc += (_, _, _) =>
            {
                return Task.FromResult(true);
            };
            proxies2.Add(proxy2);
        }

        var proxies1 = new List<ProxyServer>();

        //create a level 1 upstream proxy farm that forwards to level 2 farm
        for (var i = 0; i < 10; i++)
        {
            var proxy1 = testSuite.GetProxy();
            var proxy2 = proxies2[Random.Shared.Next() % proxies2.Count];

            proxy1.GetCustomUpStreamProxyFunc += async _ =>
            {
                var proxy = new ExternalProxy
                {
                    HostName = "localhost",
                    Port = proxy2.ProxyEndPoints[0].Port,
                    ProxyType = ExternalProxyType.Http,
                    UserName = "test_user",
                    Password = "test_password"
                };

                return await Task.FromResult(proxy);
            };

            proxies1.Add(proxy1);
        }

        var tasks = new List<Task>();

        //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
        for (var j = 0; j < 1_000; j++)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var proxy = proxies1[Random.Shared.Next() % proxies1.Count];
                    using var client = testSuite.GetClient(proxy);

                    //tests should not keep hanging indefinitely.
                    client.Timeout = TimeSpan.FromSeconds(60);
                    await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                        new StringContent("hello server. I am a client."));
                }
                //if error is thrown because of server getting overloaded its okay.
                //But client.PostAsync should'nt hang in all cases.
                catch { }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        Assert.AreEqual(1_000, tasks.Count);
        Assert.IsTrue(tasks.TrueForAll(t => t.IsCompleted),
            "Every request task must finish without hanging.");
    }
}
