using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class HttpsTests
    {
        [TestMethod]
        public async Task Can_Handle_Https_Request()
        {
            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxy = testSuite.GetProxy();
            var client = testSuite.GetClient(proxy);

            var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                        new StringContent("hello server. I am a client."));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual("I am server. I received your greetings.", body);
        }

        [TestMethod]
        public async Task Can_Handle_Https_Fake_Tunnel_Request()
        {
            var testSuite = new TestSuite();

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxy = testSuite.GetProxy();
            proxy.BeforeRequest += async (sender, e) =>
            {
                e.HttpClient.Request.Url = server.ListeningHttpUrl;
                await Task.FromResult(0);
            };

            var client = testSuite.GetClient(proxy);

            var response = await client.PostAsync(new Uri($"https://{Guid.NewGuid().ToString()}.com"),
                                        new StringContent("hello server. I am a client."));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual("I am server. I received your greetings.", body);
        }

        [TestMethod]
        public async Task Can_Handle_Https_Mutual_Tls_Request()
        {
            var testSuite = new TestSuite(true);

            var server = testSuite.GetServer();
            server.HandleRequest((context) =>
            {
                return context.Response.WriteAsync("I am server. I received your greetings.");
            });

            var proxy = testSuite.GetProxy();
            var clientCert = proxy.CertificateManager.CreateCertificate("client.com", false);
       
            proxy.ClientCertificateSelectionCallback += async (sender, e) =>
            {        
                e.ClientCertificate = clientCert;
                await Task.CompletedTask;
            };

            var client = testSuite.GetClient(proxy);

            var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                                        new StringContent("hello server. I am a client."));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual("I am server. I received your greetings.", body);
        }

    }
}
