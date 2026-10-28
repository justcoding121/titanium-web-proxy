#pragma warning disable CA1416
#pragma warning disable TWP001
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.TestSupport;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Per-cluster upstream TLS end to end: client certificate, custom CA, SNI override, hostname policy,
///     fail-closed files, QUIC accept-any, and active health probes against an mTLS origin.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UpstreamTlsTests
{
    private const string OriginName = "mtls.test";

    [TestMethod]
    [Timeout(60_000)]
    public async Task MutualTls_WithClusterCertificateAndCa_Succeeds_AndSendsTheServerNameAsSni()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        using var clientCert = pki.IssueClient("cluster-client");
        var (certPath, keyPath) = pki.WritePemPair(clientCert);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);

        using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            ClientCertificatePath = certPath,
            ClientCertificateKeyPath = keyPath,
            CaBundlePath = pki.WriteCaPem(),
            ServerName = OriginName,
        });

        using var response = await GetAsync(proxy);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "client=CN=cluster-client");
        CollectionAssert.Contains(origin.ServerNames.ToArray(), OriginName,
            "the TLS server name is the cluster's serverName, not the destination address");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PfxClientCertificate_WithPasswordFromEnvironment_Succeeds()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        using var clientCert = pki.IssueClient("pfx-client");
        const string env = "TWP_TEST_MTLS_PFX_PASSWORD";
        Environment.SetEnvironmentVariable(env, "pfx-pass");
        try
        {
            await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);
            using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
            {
                ClientCertificatePath = pki.WritePfx(clientCert, "pfx-pass"),
                ClientCertificatePasswordEnv = env,
                CaBundlePath = pki.WriteCaPem(),
                ServerName = OriginName,
            });

            using var response = await GetAsync(proxy);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "client=CN=pfx-client");
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task WrongCaBundle_Returns502()
    {
        using var pki = new UpstreamTlsTestPki();
        using var stranger = new UpstreamTlsTestPki("Stranger CA");
        using var serverCert = pki.IssueServer(OriginName);
        using var clientCert = pki.IssueClient();
        var (certPath, keyPath) = pki.WritePemPair(clientCert);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);

        using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            ClientCertificatePath = certPath,
            ClientCertificateKeyPath = keyPath,
            CaBundlePath = stranger.WriteCaPem(),
            ServerName = OriginName,
        });

        using var response = await GetAsync(proxy);

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.AreEqual(0, origin.Requests, "the proxy must not send a request after a failed validation");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task OriginRequiringClientCertificate_WithNoneConfigured_Returns502()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);

        using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            CaBundlePath = pki.WriteCaPem(),
            ServerName = OriginName,
        });

        using var response = await GetAsync(proxy);

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.AreEqual(0, origin.Requests);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task VerifyHostnameFalse_AcceptsANameMismatch_ButNotAChainError()
    {
        using var pki = new UpstreamTlsTestPki();
        using var stranger = new UpstreamTlsTestPki("Stranger CA");
        using var serverCert = pki.IssueServer(OriginName);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: false);

        // The destination is 127.0.0.1 and no serverName is set, so the certificate name cannot match.
        using (var strict = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
               {
                   CaBundlePath = pki.WriteCaPem(),
               }))
        {
            using var response = await GetAsync(strict);
            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode, "hostname checks are on by default");
        }

        using (var lenient = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
               {
                   CaBundlePath = pki.WriteCaPem(),
                   VerifyHostname = false,
               }))
        {
            using var response = await GetAsync(lenient);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        using (var wrongChain = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
               {
                   CaBundlePath = stranger.WriteCaPem(),
                   VerifyHostname = false,
               }))
        {
            using var response = await GetAsync(wrongChain);
            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
                "verifyHostname:false masks only the name mismatch");
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task UnreadableClientCertificate_FailsClosedWith502_AndNeverReachesTheOrigin()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: false);

        using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            ClientCertificatePath = System.IO.Path.Combine(pki.Directory, "missing.pem"),
            ClientCertificateKeyPath = System.IO.Path.Combine(pki.Directory, "missing.key"),
            AcceptAnyServerCertificate = true,
        });

        using var first = await GetAsync(proxy);
        using var second = await GetAsync(proxy);

        Assert.AreEqual(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadGateway, second.StatusCode);
        Assert.AreEqual(0, origin.Handshakes, "no TLS handshake may happen with a broken identity");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AcceptAnyServerCertificate_StillWorksOnTcp()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: false);

        using var proxy = await StartProxyAsync(origin.Port, new ClusterHttpClientConfig
        {
            AcceptAnyServerCertificate = true,
        });

        using var response = await GetAsync(proxy);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, origin.LastError + await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ActiveHealthProbe_UsesTheClusterIdentity_SoAnMtlsDestinationStaysHealthy()
    {
        using var pki = new UpstreamTlsTestPki();
        using var serverCert = pki.IssueServer(OriginName);
        using var clientCert = pki.IssueClient("probe-client");
        var (certPath, keyPath) = pki.WritePemPair(clientCert);
        await using var origin = new MtlsOrigin(serverCert, pki.CaCertificate, requireClientCertificate: true);

        var withIdentity = await ProbeAsync(origin.Port, new ClusterHttpClientConfig
        {
            ClientCertificatePath = certPath,
            ClientCertificateKeyPath = keyPath,
            CaBundlePath = pki.WriteCaPem(),
            ServerName = OriginName,
        });
        var errorAfterFirst = origin.LastError;
        var withoutCertificate = await ProbeAsync(origin.Port, new ClusterHttpClientConfig
        {
            CaBundlePath = pki.WriteCaPem(),
            ServerName = OriginName,
        });

        Assert.AreEqual(DestinationState.Healthy, withIdentity, "the probe presents the cluster certificate: " + errorAfterFirst + " names=" + string.Join(",", origin.ServerNames) + " requests=" + origin.Requests);
        Assert.AreEqual(DestinationState.Unhealthy, withoutCertificate,
            "control: without the client certificate the same destination is rightly ejected");
        Assert.IsTrue(origin.Requests >= 1);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Http3_AcceptAnyServerCertificate_IsHonoredByTheCentralValidation()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");

        // The origin's certificate chains to the integration test CA, which this proxy never trusts.
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "h3-accept-any")));

        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(endPoint);

        var anyConfig = new ClusterHttpClientConfig { AcceptAnyServerCertificate = true };
        var strictConfig = new ClusterHttpClientConfig { VerifyHostname = true, ServerName = "localhost" };

        var accepted = await DialQuicAsync(proxy, endPoint, origin.Port, anyConfig);
        Assert.IsNull(accepted, accepted?.ToString());

        var refused = await DialQuicAsync(proxy, endPoint, origin.Port, strictConfig);
        Assert.IsNotNull(refused, "without acceptAny the untrusted origin certificate is rejected");
    }

    private static async Task<Exception?> DialQuicAsync(ProxyServer proxy, ExplicitProxyEndPoint endPoint, int port,
        ClusterHttpClientConfig httpClient)
    {
        var material = UpstreamTlsMaterial.For(httpClient)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = MakeSession(proxy, endPoint, httpClient);
        try
        {
            var connection = await proxy.QuicConnectionPool.GetOrCreateAsync(
                "localhost", port, null, null,
                (sender, certificate, chain, errors) =>
                    proxy.ValidateServerCertificate(sender, session, certificate, chain, errors),
                cts.Token,
                sniHost: "localhost",
                failFastHandshake: false,
                tlsMaterial: material);
            await QuicConnectionPoolRelease(connection);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            session.Dispose();
        }
    }

    private static ValueTask QuicConnectionPoolRelease(QuicServerConnection connection) =>
        QuicConnectionPool.ReleaseAsync(connection);

    private static SessionEventArgs MakeSession(ProxyServer proxy, ExplicitProxyEndPoint endPoint,
        ClusterHttpClientConfig httpClient)
    {
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, System.IO.Stream.Null, proxy.BufferPool,
            cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts)
        {
            UpstreamHttpClient = httpClient,
        };
    }

    private static async Task<DestinationState> ProbeAsync(int port, ClusterHttpClientConfig httpClient)
    {
        var cluster = new ClusterConfig
        {
            Id = "probe",
            HttpClient = httpClient,
            Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = port, UseHttps = true }],
            HealthCheck = new HealthCheckConfig
            {
                Active = new ActiveHealthConfig
                {
                    Enabled = true,
                    IntervalSeconds = 3600,
                    TimeoutSeconds = 10,
                    Path = "/health",
                    UnhealthyThreshold = 1,
                    HealthyThreshold = 1,
                },
            },
        };
        var manager = new ClusterManager();
        await manager.ApplyAsync([cluster]);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        await prober.ProbeOnceAsync();
        return manager.GetDestinationState("d");
    }

    private static async Task<ProxyServer> StartProxyAsync(int originPort, ClusterHttpClientConfig httpClient)
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
        // The client speaks TLS to the proxy, so the request is HTTPS and the upstream leg is TLS too.
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

    private static async Task<HttpResponseMessage> GetAsync(ProxyServer proxy)
    {
        var endPoint = proxy.ProxyEndPoints[0];
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await http.GetAsync($"https://localhost:{endPoint.Port}/");
    }
    /// <summary>Loopback HTTPS origin that can demand a client certificate and records SNI and handshakes.</summary>
    private sealed class MtlsOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 serverCertificate;
        private readonly X509Certificate2 clientRoot;
        private readonly bool requireClientCertificate;
        private readonly Task acceptLoop;
        private int requests;
        private int handshakes;

        public MtlsOrigin(X509Certificate2 serverCertificate, X509Certificate2 clientRoot, bool requireClientCertificate)
        {
            this.serverCertificate = serverCertificate;
            this.clientRoot = clientRoot;
            this.requireClientCertificate = requireClientCertificate;
            listener.Start();
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> ServerNames { get; } = new();

        public string? LastError { get; private set; }

        public int Requests => Volatile.Read(ref requests);

        public int Handshakes => Volatile.Read(ref handshakes);

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
            try
            {
                await using var ssl = new SslStream(client.GetStream(), false);
                var options = new SslServerAuthenticationOptions
                {
                    ClientCertificateRequired = requireClientCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        if (!string.IsNullOrEmpty(name)) ServerNames.Enqueue(name);
                        return serverCertificate;
                    },
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate == null) return !requireClientCertificate;
                        return ChainsToClientRoot(certificate);
                    },
                };
                await ssl.AuthenticateAsServerAsync(options, stop.Token);
                Interlocked.Increment(ref handshakes);

                var buffer = new byte[4096];
                var read = 0;
                var text = new StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    read = await ssl.ReadAsync(buffer, stop.Token);
                    if (read == 0) return;
                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Interlocked.Increment(ref requests);
                var who = ssl.RemoteCertificate?.Subject ?? "none";
                var body = $"client={who};sni={ssl.TargetHostName}";
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n" +
                           "Connection: close\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(head + body), stop.Token);
                await ssl.FlushAsync(stop.Token);
            }
            catch (Exception ex)
            {
                // A rejected handshake (wrong CA, missing client certificate) ends here by design.
                LastError = ex.ToString();
            }
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

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Shutting down.
            }

            stop.Dispose();
        }
    }
}
