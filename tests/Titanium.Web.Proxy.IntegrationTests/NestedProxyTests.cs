using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class NestedProxyTests
    {
        [TestMethod]
        public async Task Smoke_Test_Nested_Proxy()
        {
            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxy1 = testSuite.GetProxy();
            var proxy2 = testSuite.GetProxy(proxy1);

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
            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxy1 = testSuite.GetProxy();
            proxy1.ProxyBasicAuthenticateFunc = async (session, username, password) =>
            {
                session.UserData = "Test";
                return await Task.FromResult(true);
            };

            var proxy2 = testSuite.GetProxy();

            proxy1.GetCustomUpStreamProxyFunc = async (session) =>
            {
                Assert.AreEqual("Test", session.UserData);

                return await Task.FromResult(new Models.ExternalProxy("localhost", proxy2.ProxyEndPoints[0].Port));
            };

            var client = testSuite.GetClient(proxy1, true);

            var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                        new StringContent("hello server. I am a client."));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual("I am server. I received your greetings.", body);
        }

        [TestMethod]
        [Timeout(2 * 60 * 1000)]
        public async Task Nested_Proxy_Farm_Without_Connection_Cache_Should_Not_Hang()
        {
            var rnd = new Random();

            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxies2 = new List<ProxyServer>();

            //create a level 2 upstream proxy farm that forwards to server
            for (int i = 0; i < 10; i++)
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
            for (int i = 0; i < 10; i++)
            {
                var proxy1 = testSuite.GetProxy();
                proxy1.EnableConnectionPool = false;
                var proxy2 = proxies2[rnd.Next() % proxies2.Count];

                var explicitEndpoint = proxy1.ProxyEndPoints.OfType<ExplicitProxyEndPoint>().First();
                explicitEndpoint.BeforeTunnelConnectRequest += (_, e) =>
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = "localhost",
                        Port = proxy2.ProxyEndPoints[0].Port,
                        ProxyType = ExternalProxyType.Http,
                        UserName = "test_user",
                        Password = "test_password"
                    };

                    return Task.CompletedTask;
                };

                proxy1.BeforeRequest += (_, e) =>
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = "localhost",
                        Port = proxy2.ProxyEndPoints[0].Port,
                        ProxyType = ExternalProxyType.Http,
                        UserName = "test_user",
                        Password = "test_password"
                    };

                    return Task.CompletedTask;
                };

                proxies1.Add(proxy1);
            }

            var tasks = new List<Task>();

            //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
            for (int j = 0; j < 10_000; j++)
            {
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var proxy = proxies1[rnd.Next() % proxies1.Count];
                        using var client = testSuite.GetClient(proxy);

                        //tests should not keep hanging for 30 mins.
                        client.Timeout = TimeSpan.FromMinutes(30);
                        await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                                    new StringContent("hello server. I am a client."));

                    }
                    //if error is thrown because of server overloading its okay.
                    //But client.PostAsync should'nt hang in all cases.
                    catch { }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }


        //Reproduce bug reported so that we can fix it.
        //https://github.com/justcoding121/titanium-web-proxy/issues/826
        [TestMethod]
        [Timeout(2 * 60 * 1000)]
        public async Task Nested_Proxy_Farm_With_Connection_Cache_Should_Not_Hang()
        {
            var rnd = new Random();

            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxies2 = new List<ProxyServer>();

            //create a level 2 upstream proxy farm that forwards to server
            for (int i = 0; i < 10; i++)
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
            for (int i = 0; i < 10; i++)
            {
                var proxy1 = testSuite.GetProxy();
                //proxy1.EnableConnectionPool = false;
                var proxy2 = proxies2[rnd.Next() % proxies2.Count];

                var explicitEndpoint = proxy1.ProxyEndPoints.OfType<ExplicitProxyEndPoint>().First();
                explicitEndpoint.BeforeTunnelConnectRequest += (_, e) =>
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = "localhost",
                        Port = proxy2.ProxyEndPoints[0].Port,
                        ProxyType = ExternalProxyType.Http,
                        UserName = "test_user",
                        Password = "test_password"
                    };

                    return Task.CompletedTask;
                };

                proxy1.BeforeRequest += (_, e) =>
                {
                    e.CustomUpStreamProxy = new ExternalProxy()
                    {
                        HostName = "localhost",
                        Port = proxy2.ProxyEndPoints[0].Port,
                        ProxyType = ExternalProxyType.Http,
                        UserName = "test_user",
                        Password = "test_password"
                    };

                    return Task.CompletedTask;
                };

                proxies1.Add(proxy1);
            }

            var tasks = new List<Task>();

            //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
            for (int j = 0; j < 10_000; j++)
            {
                var task = Task.Run(async () =>
                 {
                     try
                     {
                         var proxy = proxies1[rnd.Next() % proxies1.Count];
                         using var client = testSuite.GetClient(proxy);

                         //tests should not keep hanging for 30 mins.
                         client.Timeout = TimeSpan.FromMinutes(30);
                         await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                                     new StringContent("hello server. I am a client."));
                     }
                     //if error is thrown because of server overloading its okay.
                     //But client.PostAsync should'nt hang in all cases.
                     catch { }
                 });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }
    }
}
