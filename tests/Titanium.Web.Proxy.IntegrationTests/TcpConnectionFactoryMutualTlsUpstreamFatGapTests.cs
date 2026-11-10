#pragma warning disable CA1416
#pragma warning disable TWP001
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.TestSupport;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: mutual TLS upstream with a cluster client certificate.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryMutualTlsUpstreamFatGapTests
{
    private const string OriginName = "mtls-wave34.test";

    /// <summary>
    ///     Bug class: origin requiring a client certificate fails even when the cluster supplies PEM credentials.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task ClusterClientCert_OriginRequiresMtls_Succeeds()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        using var clientCert = pki.IssueClient("wave34-client");
        var (certPath, keyPath) = pki.WritePemPair(clientCert);
        await using var origin = new MtlsLoopbackOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);

        using var proxy = await StartClusterProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            ClientCertificatePath = certPath,
            ClientCertificateKeyPath = keyPath,
            CaBundlePath = pki.WriteCaPem(),
            ServerName = OriginName,
        });

        using var response = await GetThroughProxyAsync(proxy);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "client=CN=wave34-client");
        Assert.IsTrue(origin.Handshakes >= 1);
    }

    private static async Task<ProxyServer> StartClusterProxyAsync(int originPort, ClusterHttpClientConfig httpClient)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                HttpClient = httpClient,
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = originPort, UseHttps = true },
                ],
            },
        ]);
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        });
        proxy.Start();
        return proxy;
    }

    private static async Task<HttpResponseMessage> GetThroughProxyAsync(ProxyServer proxy)
    {
        var port = proxy.ProxyEndPoints[0].Port;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await http.GetAsync($"https://127.0.0.1:{port}/");
    }

    private sealed class MtlsLoopbackOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 serverCertificate;
        private readonly X509Certificate2 clientRoot;
        private readonly bool requireClientCertificate;
        private readonly Task acceptLoop;
        private int handshakes;

        public MtlsLoopbackOrigin(X509Certificate2 serverCertificate, X509Certificate2 clientRoot, bool requireClientCertificate)
        {
            this.serverCertificate = serverCertificate;
            this.clientRoot = clientRoot;
            this.requireClientCertificate = requireClientCertificate;
            listener.Start();
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Handshakes => Volatile.Read(ref handshakes);

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            try { await acceptLoop; } catch { /* shutting down */ }
            stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            await using var ssl = new SslStream(client.GetStream(), false);
            var options = new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = requireClientCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                ServerCertificate = serverCertificate,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate == null) return !requireClientCertificate;
                    return ChainsToClientRoot(certificate);
                },
            };
            await ssl.AuthenticateAsServerAsync(options, stop.Token);
            Interlocked.Increment(ref handshakes);
            var clientSubject = ssl.RemoteCertificate?.Subject ?? "(none)";
            var body = $"client={clientSubject}";
            var response =
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length +
                "\r\nConnection: close\r\n\r\n" + body;
            var bytes = System.Text.Encoding.ASCII.GetBytes(response);
            await ssl.WriteAsync(bytes, stop.Token);
        }

        private bool ChainsToClientRoot(X509Certificate certificate)
        {
            using var leaf = new X509Certificate2(certificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(clientRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(leaf);
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
