using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Clusters;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.TestSupport;

namespace Titanium.Plus.Tests;

[TestClass]
public class ActiveHealthProbeUpstreamTlsTests
{
    private const string OriginName = "mtls.test";

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
        using var prober = new ActiveHealthProber(manager, cluster, TimeProvider.System);
        await prober.ProbeOnceAsync();
        return manager.GetDestinationState("d");
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
