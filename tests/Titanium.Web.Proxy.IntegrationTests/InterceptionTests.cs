using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.IntegrationTests
{
    [TestClass]
    public class InterceptionTests
    {
        [TestMethod]
        public async Task Can_Intercept_Post_Requests()
        {
            string testHost = "localhost";

            using (var proxy = new ProxyTestController())
            {
                var serverCertificate = await proxy.CertificateManager.CreateServerCertificate(testHost);

                using (var server = new Server(serverCertificate))
                {
                    var testUrl = $"http://{testHost}:{server.HttpListeningPort}";
                    using (var client = TestHelper.CreateHttpClient(proxy.ListeningPort))
                    {
                        var response = await client.PostAsync(new Uri(testUrl), new StringContent("hello!"));

                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                        var body = await response.Content.ReadAsStringAsync();
                        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
                    }
                }
            }
        }

        private class ProxyTestController : IDisposable
        {
            private readonly ProxyServer proxyServer;
            public int ListeningPort => proxyServer.ProxyEndPoints[0].Port;
            public CertificateManager CertificateManager => proxyServer.CertificateManager;

            public ProxyTestController()
            {
                proxyServer = new ProxyServer();
                var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 0, true);
                proxyServer.AddEndPoint(explicitEndPoint);
                proxyServer.BeforeRequest += OnRequest;
                proxyServer.Start();
            }

            public async Task OnRequest(object sender, SessionEventArgs e)
            {
                if (e.HttpClient.Request.Url.Contains("localhost"))
                {
                    if (e.HttpClient.Request.HasBody)
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

        private class Server : IDisposable
        {
            public int HttpListeningPort;
            public int HttpsListeningPort;

            private IWebHost host;
            public Server(X509Certificate2 serverCertificate)
            {
                host = new WebHostBuilder()
                            .UseKestrel(options =>
                            {
                                options.Listen(IPAddress.Loopback, 0);
                                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                                {
                                    listenOptions.UseHttps(serverCertificate);
                                });
                            })
                            .UseStartup<Startup>()
                            .Build();

                host.Start();

                string httpAddress = host.ServerFeatures
                            .Get<IServerAddressesFeature>()
                            .Addresses
                            .First();

                string httpsAddress = host.ServerFeatures
                            .Get<IServerAddressesFeature>()
                            .Addresses
                            .Skip(1)
                            .First();

                HttpListeningPort = int.Parse(httpAddress.Split(':')[2]);
                HttpsListeningPort = int.Parse(httpsAddress.Split(':')[2]);
            }

            public void Dispose()
            {
                host.Dispose();
            }

            private class Startup
            {
                public void Configure(IApplicationBuilder app)
                {
                    app.Run(context =>
                    {
                        return context.Response.WriteAsync("Server received you request.");
                    });

                }
            }
        }

    }
}