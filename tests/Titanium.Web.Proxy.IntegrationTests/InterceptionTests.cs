using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class InterceptionTests
    {
        [TestMethod]
        public async Task Can_Intercept_Post_Requests()
        {
            string testUrl = "http://interceptthis.com";

            using (var proxy = new ProxyTestController())
            {
                using (var client = TestHelper.CreateHttpClient(testUrl, proxy.ListeningPort))
                {
                    var response = await client.PostAsync(new Uri(testUrl), new StringContent("hello!"));

                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                    var body = await response.Content.ReadAsStringAsync();
                    Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
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
                proxyServer.CertificateManager.RootCertificateName = "root-certificate-for-integration-test.pfx";
                var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 0, true);
                proxyServer.AddEndPoint(explicitEndPoint);
                proxyServer.BeforeRequest += OnRequest;
                proxyServer.Start();
            }

            public async Task OnRequest(object sender, SessionEventArgs e)
            {
                if (e.WebSession.Request.Url.Contains("interceptthis.com"))
                {
                    if (e.WebSession.Request.HasBody)
                    {
                        var body = await e.GetRequestBodyAsString();
                    }

                    e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                    return;
                }

                await Task.FromResult(0);
            }

            public void Dispose()
            {
                proxyServer.BeforeRequest -= OnRequest;
                proxyServer.Stop();
                proxyServer.Dispose();
            }
        }

    }
}