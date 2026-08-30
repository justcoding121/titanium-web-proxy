using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Options;

#pragma warning disable TWP001

namespace Titanium.Cli.Tests;

[TestClass]
public class ServerConfigApplierTests
{
    [TestMethod]
    public void Apply_NullServer_IsNoOp()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var before = proxy.EnableHttp2;
        ServerConfigApplier.Apply(proxy, null);
        Assert.AreEqual(before, proxy.EnableHttp2);
    }

    [TestMethod]
    public void Apply_PublicFacingProfile_SetsAdmissionAndTimeouts()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ServerConfigApplier.Apply(proxy, new ServerConfig { Profile = "PublicFacing" });

        Assert.AreEqual(ProxyProfile.PublicFacing, proxy.Profile);
        Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
        Assert.AreEqual(10_000, proxy.MaxConcurrentClientConnections);
        Assert.IsTrue(proxy.ClientHeaderTimeoutSeconds > 0);
        Assert.IsTrue(proxy.RequestTimeoutSeconds > 0);
    }

    [TestMethod]
    public void Apply_TimeoutOverlays_AfterProfile()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ServerConfigApplier.Apply(proxy, new ServerConfig
        {
            Profile = "PublicFacing",
            Timeouts = new TimeoutsConfig
            {
                ClientHeaderTimeoutSeconds = 5,
                RequestTimeoutSeconds = 90,
            },
        });

        Assert.AreEqual(5, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(90, proxy.RequestTimeoutSeconds);
        Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
    }

    [TestMethod]
    public void Apply_EnableHttp2False()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Assert.IsTrue(proxy.EnableHttp2);
        ServerConfigApplier.Apply(proxy, new ServerConfig { EnableHttp2 = false });
        Assert.IsFalse(proxy.EnableHttp2);
    }

    [TestMethod]
    public void Apply_ProtocolFlags_TimeoutsPoolingLimitsPolicyTlsUpstreamAndCerts()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ServerConfigApplier.Apply(proxy, new ServerConfig
        {
            EnableHttp3 = false,
            EnableRfc8441 = false,
            EnableQpackDynamicTable = false,
            EnableHttpsSvcbDnsDiscovery = false,
            Enable100ContinueBehaviour = true,
            CompatibilityMode100Continue = true,
            EnableWinAuth = false,
            OriginHttpVersionPolicy = "NormalizeToHttp11",
            ViaHeaderPseudonym = "twp-test",
            BlockPrivateNetworkDestinations = true,
            CheckCertificateRevocation = "Offline",
            DnsServerEndPoint = "8.8.8.8:53",
            Timeouts = new TimeoutsConfig
            {
                ConnectionTimeOutSeconds = 11,
                ConnectTimeOutSeconds = 12,
                ClientHeaderTimeoutSeconds = 13,
                ResponseHeaderTimeoutSeconds = 14,
                IdleReadTimeoutSeconds = 15,
                IdleWriteTimeoutSeconds = 16,
                RequestTimeoutSeconds = 17,
                NetworkFailureRetryAttempts = 3,
            },
            Pooling = new PoolingConfig
            {
                EnableConnectionPool = true,
                EnableTcpServerConnectionPrefetch = false,
                EnableIpv6UnreachableSoftSkip = true,
                MaxCachedConnections = 9,
                MaxConcurrentHttp11HttpsOriginCreates = 4,
                MaxConcurrentClientConnections = 123,
                NoDelay = true,
                EnableTcpKeepAlive = true,
                TcpTimeWaitSeconds = 30,
                ListenerBackLog = 64,
                ReuseSocket = true,
                ThreadPoolWorkerThread = 8,
            },
            Limits = new LimitsConfig
            {
                MaxHeaderLineBytes = 2048,
                MaxHeaderCount = 50,
                MaxHeaderAggregateBytes = 8192,
                MaxEncodedBodyBytes = 1_000_000,
                MaxDecodedBodyBytes = 2_000_000,
                MaxDecompressionRatio = 12.5,
                MaxConcurrentClients = 40,
                MaxConcurrentStreamsPerConnection = 20,
                MaxPeerInitiatedIncompleteStreamResets = 7,
                MaxOpenHeaderBlockFrames = 5,
                MaxOpenHeaderBlockDurationSeconds = 9,
                ConnectionPoolingEnabled = true,
                MaxCachedConnectionsPerHost = 6,
                MaxOriginHttp2ConnectionsPerAuthority = 3,
                MaxCertificateCacheEntries = 100,
                MaxCertificateDiskCacheEntries = 50,
                MaxBufferedBodyBytes = 4096,
                MaxDecodedHeaderListBytes = 1024,
                MaxWebSocketFramePayloadBytes = 65536,
            },
            PolicyModes = new PolicyModesConfig
            {
                BodyBudget = "Observe",
                DecompressionRatio = "Enforce",
                HeaderLimits = "Observe",
                AdmissionControl = "Enforce",
                Http2AbuseBudget = "Disabled",
                AllowAmbiguousFraming = true,
            },
            Tls = new TlsConfig
            {
                SupportedSslProtocols = ["Tls12", "Tls13"],
                SupportedServerSslProtocols = ["Tls13"],
            },
            Upstream = new UpstreamConfig
            {
                ForwardToUpstreamGateway = true,
                UpstreamProxyConfigurationScript = "https://example.test/proxy.pac",
                HttpProxy = new ExternalProxyConfig
                {
                    HostName = "proxy.example",
                    Port = 8080,
                    UserName = "u",
                    Password = "p",
                    ProxyType = "Http",
                    BypassLocalhost = true,
                    ProxyDnsRequests = true,
                    NextHop = new ExternalProxyConfig
                    {
                        HostName = "next.example",
                        Port = 1080,
                        ProxyType = "Socks5",
                    },
                },
                HttpsProxy = new ExternalProxyConfig
                {
                    HostName = "secure-proxy.example",
                    Port = 8443,
                    ProxyType = "Http",
                },
                UpStreamEndPoint = "0.0.0.0:0",
                UpStreamEndPointIPv4 = "127.0.0.1:0",
                UpStreamEndPointIPv6 = "[::1]:0",
            },
            CertificateManager = new CertificateManagerConfig
            {
                CertificateEngine = "BouncyCastle",
                LeafCertificateKeyAlgorithm = "Rsa2048",
                PfxFilePath = "certs/root.pfx",
                PfxPassword = "secret",
                OverwritePfxFile = true,
                CertificateValidDays = 365,
                CertificateGraceDays = 7,
                CertificateCacheTimeOutMinutes = 60,
                RootCertificateName = "TWP Test Root",
                RootCertificateIssuerName = "TWP Test Issuer",
                SaveFakeCertificates = true,
                DisableWildCardCertificates = true,
            },
        });

        Assert.IsFalse(proxy.EnableHttp3);
        Assert.IsFalse(proxy.EnableRfc8441);
        Assert.IsFalse(proxy.EnableQpackDynamicTable);
        Assert.IsFalse(proxy.EnableHttpsSvcbDnsDiscovery);
        Assert.IsTrue(proxy.Enable100ContinueBehaviour);
        Assert.IsTrue(proxy.CompatibilityMode100Continue);
        Assert.IsFalse(proxy.EnableWinAuth);
        Assert.AreEqual(OriginHttpVersionPolicy.NormalizeToHttp11, proxy.OriginHttpVersionPolicy);
        Assert.AreEqual("twp-test", proxy.ViaHeaderPseudonym);
        Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
        Assert.AreEqual(X509RevocationMode.Offline, proxy.CheckCertificateRevocation);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53), proxy.DnsServerEndPoint);

        Assert.AreEqual(11, proxy.ConnectionTimeOutSeconds);
        Assert.AreEqual(12, proxy.ConnectTimeOutSeconds);
        Assert.AreEqual(13, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(14, proxy.ResponseHeaderTimeoutSeconds);
        Assert.AreEqual(15, proxy.IdleReadTimeoutSeconds);
        Assert.AreEqual(16, proxy.IdleWriteTimeoutSeconds);
        Assert.AreEqual(17, proxy.RequestTimeoutSeconds);
        Assert.AreEqual(3, proxy.NetworkFailureRetryAttempts);

        Assert.IsTrue(proxy.EnableConnectionPool);
        Assert.IsFalse(proxy.EnableTcpServerConnectionPrefetch);
        Assert.IsTrue(proxy.EnableIpv6UnreachableSoftSkip);
        Assert.AreEqual(9, proxy.MaxCachedConnections);
        Assert.AreEqual(4, proxy.MaxConcurrentHttp11HttpsOriginCreates);
        Assert.AreEqual(123, proxy.MaxConcurrentClientConnections);
        Assert.IsTrue(proxy.NoDelay);
        Assert.IsTrue(proxy.EnableTcpKeepAlive);
        Assert.AreEqual(30, proxy.TcpTimeWaitSeconds);
        Assert.AreEqual(64, proxy.ListenerBackLog);
        Assert.IsTrue(proxy.ReuseSocket);
        Assert.AreEqual(8, proxy.ThreadPoolWorkerThread);

        Assert.AreEqual(2048, proxy.ResourceLimits.MaxHeaderLineBytes);
        Assert.AreEqual(50, proxy.ResourceLimits.MaxHeaderCount);
        Assert.AreEqual(4096, proxy.MaxBufferedBodyBytes);
        Assert.AreEqual(1024, proxy.MaxDecodedHeaderListBytes);
        Assert.AreEqual(65536, proxy.MaxWebSocketFramePayloadBytes);

        Assert.AreEqual(PolicyMode.Observe, proxy.PolicyModes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Enforce, proxy.PolicyModes[PolicyFamily.DecompressionRatio]);
        Assert.IsTrue(proxy.PolicyModes.AllowAmbiguousFraming);

        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, proxy.SupportedSslProtocols);
        Assert.AreEqual(SslProtocols.Tls13, proxy.SupportedServerSslProtocols);

        Assert.IsTrue(proxy.ForwardToUpstreamGateway);
        Assert.AreEqual(new Uri("https://example.test/proxy.pac"), proxy.UpstreamProxyConfigurationScript);
        Assert.IsNotNull(proxy.UpStreamHttpProxy);
        Assert.AreEqual("proxy.example", proxy.UpStreamHttpProxy!.HostName);
        Assert.AreEqual(8080, proxy.UpStreamHttpProxy.Port);
        Assert.IsNotNull(proxy.UpStreamHttpProxy.NextHop);
        Assert.AreEqual(ExternalProxyType.Socks5, proxy.UpStreamHttpProxy.NextHop!.ProxyType);
        Assert.IsNotNull(proxy.UpStreamHttpsProxy);
        Assert.AreEqual(new IPEndPoint(IPAddress.Any, 0), proxy.UpStreamEndPoint);
        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 0), proxy.UpStreamEndPointIPv4);
        Assert.AreEqual(new IPEndPoint(IPAddress.IPv6Loopback, 0), proxy.UpStreamEndPointIPv6);

        Assert.AreEqual(CertificateEngine.BouncyCastle, proxy.CertificateManager.CertificateEngine);
        Assert.AreEqual(CertificateKeyAlgorithm.Rsa2048, proxy.CertificateManager.LeafCertificateKeyAlgorithm);
        Assert.AreEqual("certs/root.pfx", proxy.CertificateManager.PfxFilePath);
        Assert.AreEqual("secret", proxy.CertificateManager.PfxPassword);
        Assert.IsTrue(proxy.CertificateManager.OverwritePfxFile);
        Assert.AreEqual(365, proxy.CertificateManager.CertificateValidDays);
        Assert.AreEqual(7, proxy.CertificateManager.CertificateGraceDays);
        Assert.AreEqual(60, proxy.CertificateManager.CertificateCacheTimeOutMinutes);
        Assert.AreEqual("TWP Test Root", proxy.CertificateManager.RootCertificateName);
        Assert.AreEqual("TWP Test Issuer", proxy.CertificateManager.RootCertificateIssuerName);
        Assert.IsTrue(proxy.CertificateManager.SaveFakeCertificates);
        Assert.IsTrue(proxy.CertificateManager.DisableWildCardCertificates);
    }

    [TestMethod]
    public void TryParseSslProtocols_OrsFlags()
    {
        Assert.IsTrue(ServerConfigApplier.TryParseSslProtocols(["Tls12", "Tls13"], out var protocols));
        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, protocols);
    }

    [TestMethod]
    public void TryParseSslProtocols_Invalid_ReturnsFalse()
    {
        Assert.IsFalse(ServerConfigApplier.TryParseSslProtocols(["Tls12", "Nope"], out var protocols));
        Assert.AreEqual(SslProtocols.None, protocols);
    }

    [TestMethod]
    public void TryParseEndPoint_IPv4()
    {
        Assert.IsTrue(ServerConfigApplier.TryParseEndPoint("127.0.0.1:53", out var ep));
        Assert.AreEqual(53, ep.Port);
        Assert.AreEqual(IPAddress.Loopback, ep.Address);
    }

    [TestMethod]
    public void TryParseEndPoint_IPv6Bracketed()
    {
        Assert.IsTrue(ServerConfigApplier.TryParseEndPoint("[::1]:443", out var ep));
        Assert.AreEqual(443, ep.Port);
        Assert.AreEqual(IPAddress.IPv6Loopback, ep.Address);
    }

    [TestMethod]
    public void TryParseEndPoint_Invalid_ReturnsFalse()
    {
        Assert.IsFalse(ServerConfigApplier.TryParseEndPoint("not-an-endpoint", out _));
        Assert.IsFalse(ServerConfigApplier.TryParseEndPoint("", out _));
    }
}
