using System;
using System.Collections.Generic;
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

        //Try reproduce bug reported so that we can fix it.
        //https://github.com/justcoding121/titanium-web-proxy/issues/826
        [TestMethod]
        public async Task Nested_Proxy_Farm_Should_Not_Hang()
        {
            var rnd = new Random();

            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxies2 = new List<ProxyServer>();

            //create a level 2 upstream proxy farm 
            for (int i = 0; i < 5; i++)
            {
                var proxy = testSuite.GetProxy();
                proxy.ProxyBasicAuthenticateFunc += (_, _, _) =>
                {
                    return Task.FromResult(true);
                };

                proxies2.Add(proxy);
            }

            var proxies1 = new List<ProxyServer>();

            //create a level 1 upstream proxy farm
            for (int i = 0; i < 5; i++)
            {
                var proxy1 = testSuite.GetProxy();
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
            for (int j = 0; j < 1000; j++)
            {
                var task = Task.Run(async () =>
                 {
                     var proxy = proxies1[rnd.Next() % proxies1.Count];
                     using var client = testSuite.GetClient(proxy);
                     client.Timeout = TimeSpan.FromMinutes(10);
                     var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                                 new StringContent("hello server. I am a client."));

                     Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                     var body = await response.Content.ReadAsStringAsync();

                     Assert.AreEqual("I am server. I received your greetings.", body);
                 });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }
    }
}
