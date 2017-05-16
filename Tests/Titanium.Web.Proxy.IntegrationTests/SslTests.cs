using System;
using System.Diagnostics;
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
        public void TestSsl()
        {
            //expand this to stress test to find
            //why in long run proxy becomes unresponsive as per issue #184
            var testUrl = "https://google.com";
            int proxyPort = 8086;
            var proxy = new ProxyTestController();
            proxy.StartProxy(proxyPort);

            using (var client = CreateHttpClient(testUrl, proxyPort))
            {
                var response = client.GetAsync(new Uri(testUrl)).Result;
            }
        }

        private HttpClient CreateHttpClient(string url, int localProxyPort)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://localhost:{localProxyPort}", false),
                UseProxy = true,
            };

            var client = new HttpClient(handler);

            return client;
        }
    }

    public class ProxyTestController
    {
        private readonly ProxyServer proxyServer;

        public ProxyTestController()
        {
            proxyServer = new ProxyServer();
            proxyServer.TrustRootCertificate = true;
        }

        public void StartProxy(int proxyPort)
        {
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, proxyPort, true);

            //An explicit endpoint is where the client knows about the existance of a proxy
            //So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();

            foreach (var endPoint in proxyServer.ProxyEndPoints)
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ",
                    endPoint.GetType().Name, endPoint.IpAddress, endPoint.Port);
        }

        public void Stop()
        {
            proxyServer.BeforeRequest -= OnRequest;
            proxyServer.BeforeResponse -= OnResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();
        }

        //intecept & cancel, redirect or update requests
        public async Task OnRequest(object sender, SessionEventArgs e)
        {
            Debug.WriteLine(e.WebSession.Request.Url);
            await Task.FromResult(0);
        }

        //Modify response
        public async Task OnResponse(object sender, SessionEventArgs e)
        {
            await Task.FromResult(0);
        }

        /// <summary>
        /// Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            //set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            //set e.clientCertificate to override

            return Task.FromResult(0);
        }
    }
}
