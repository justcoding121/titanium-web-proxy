using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class SslTests
    {
        [TestMethod]
        public async Task TestSsl()
        {
            string testUrl = "https://google.com";
            using (var proxy = new ProxyTestController())
            {
                using (var client = TestHelper.CreateHttpClient(testUrl, proxy.ListeningPort))
                {
                    var response = await client.GetAsync(new Uri(testUrl));
                    Assert.IsNotNull(response);
                }
            }
        }

        private class ProxyTestController : IDisposable
        {
            private readonly ProxyServer proxyServer;
            public int ListeningPort => proxyServer.ProxyEndPoints[0].Port;

            public ProxyTestController()
            {
                proxyServer = new ProxyServer();
                var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 0, true);
                proxyServer.AddEndPoint(explicitEndPoint);
                proxyServer.Start();
            }

            public void Dispose()
            {
                proxyServer.Stop();
                proxyServer.Dispose();
            }
        }
    }
}
