using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
[DoNotParallelize]
public class ClientCertificateModeTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Allow_WithoutCertificate_Succeeds_AndWithCertificate_SetsHeader()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var accept = Task.Run(() => ServeEchoCertAsync(origin, cts.Token), cts.Token);
        using var clientCert = CreateClientCertificate();

        using var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
            ClientCertificateMode = ClientCertificateMode.Allow,
        };
        proxy.AddEndPoint(endPoint);
        await AttachReverseRouteAsync(proxy, originPort);
        proxy.Start();

        try
        {
            var without = await GetAsync(endPoint.Port, clientCertificate: null);
            StringAssert.Contains(without, "cert:");
            Assert.IsFalse(without.Contains("CN=client-allow", StringComparison.Ordinal));

            var withCert = await GetAsync(endPoint.Port, clientCert);
            StringAssert.Contains(withCert, "CN=client-allow");
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Require_WithoutCertificate_IsRefused()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
            ForwardCleartext = true,
            ClientCertificateMode = ClientCertificateMode.Require,
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        try
        {
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => GetAsync(endPoint.Port, clientCertificate: null));
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static async Task AttachReverseRouteAsync(ProxyServer proxy, int originPort)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig
                    {
                        Id = "d",
                        Address = "127.0.0.1",
                        Port = originPort,
                        UseDestinationHost = true,
                    },
                ],
            },
        ]);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Transforms =
                    [
                        new TransformConfig
                        {
                            Kind = TransformKinds.ClientCertHeader,
                            Parameters = new Dictionary<string, string> { ["name"] = "X-Client-Cert" },
                        },
                    ],
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
    }

    private static async Task<string> GetAsync(int port, X509Certificate2? clientCertificate)
    {
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
        };
        if (clientCertificate is not null)
        {
            handler.ClientCertificates.Add(clientCertificate);
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return await client.GetStringAsync($"https://127.0.0.1:{port}/tls");
    }

    private static X509Certificate2 CreateClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=client-allow", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static (HttpListener Listener, int Port) BindLoopbackHttpListener()
    {
        Exception? last = null;
        for (var i = 0; i < 8; i++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind HttpListener.", last);
    }

    private static async Task ServeEchoCertAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                var cert = ctx.Request.Headers["X-Client-Cert"] ?? "";
                var body = Encoding.UTF8.GetBytes("cert:" + cert);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
