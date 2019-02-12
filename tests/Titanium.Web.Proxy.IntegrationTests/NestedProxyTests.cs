using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

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

    }
}
