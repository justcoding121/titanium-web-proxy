using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
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
            int proxyPort = 8086;
            var proxy = new ProxyTestController();
            proxy.StartProxy(proxyPort);

            using (var client = TestHelper.CreateHttpClient(testUrl, proxyPort))
            {
                var response = await client.GetAsync(new Uri(testUrl));
                Assert.IsNotNull(response);
            }
        }
      

        private class ProxyTestController
        {
            private readonly ProxyServer proxyServer;

            public ProxyTestController()
            {
                proxyServer = new ProxyServer();
                proxyServer.CertificateManager.RootCertificateName = "root-certificate-for-integration-test.pfx";
            }

            public void StartProxy(int proxyPort)
            {
                var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, proxyPort, true);
                proxyServer.AddEndPoint(explicitEndPoint);
                proxyServer.Start();
            }

            public void Stop()
            {
                proxyServer.Stop();
                proxyServer.Dispose();
            }

        }
    }
}
