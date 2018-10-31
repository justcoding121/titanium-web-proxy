using System;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class InterceptionTests
    {
        [TestMethod]
        public void CanInterceptPostRequests()
        {
            string testUrl = "http://interceptthis.com";
            int proxyPort = 8086;
            var proxy = new ProxyTestController();
            proxy.StartProxy(proxyPort);

            using (var client = CreateHttpClient(testUrl, proxyPort))
            {
                var response = client.PostAsync(new Uri(testUrl), new StringContent("hello!")).Result;

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var body = response.Content.ReadAsStringAsync().Result;
                Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
            }
        }

        private HttpClient CreateHttpClient(string url, int localProxyPort)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://localhost:{localProxyPort}", false),
                UseProxy = true
            };

            var client = new HttpClient(handler);

            return client;
        }

    }
}