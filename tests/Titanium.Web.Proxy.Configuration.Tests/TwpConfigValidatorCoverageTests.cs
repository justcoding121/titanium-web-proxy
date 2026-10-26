using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class TwpConfigValidatorCoverageTests
{
    [TestMethod]
    public void Validate_ReportsEveryStructuralErrorArm()
    {
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig { Id = "", Destinations = [] },
                new ClusterConfig { Id = "dup", Destinations = [] },
                new ClusterConfig { Id = "dup", Destinations = [] },
            ],
            Routes =
            [
                new RouteConfig { Id = "", ClusterId = "missing", Match = null! },
                new RouteConfig { Id = "r1", ClusterId = "", Match = null! },
            ],
            Listeners =
            [
                new ListenerConfig
                {
                    Port = 0,
                    Type = "ftp",
                    MaxCachedConnections = 0,
                    MaxConcurrentClients = 0,
                    HandshakeTimeoutSeconds = -1,
                    IdleTimeoutSeconds = -1,
                },
                new ListenerConfig { Port = 70000, Type = "explicit" },
            ],
            Server = new ServerConfig
            {
                Profile = "NotAProfile",
                OriginHttpVersionPolicy = "Http3Only",
                CheckCertificateRevocation = "NotAMode",
                AccessLog = new AccessLogConfig { Path = "", SampleRate = 2 },
                Timeouts = new TimeoutsConfig
                {
                    ConnectionTimeOutSeconds = -1,
                    ConnectTimeOutSeconds = -1,
                    ClientHeaderTimeoutSeconds = -1,
                    ResponseHeaderTimeoutSeconds = -1,
                    IdleReadTimeoutSeconds = -1,
                    IdleWriteTimeoutSeconds = -1,
                    RequestTimeoutSeconds = -1,
                    NetworkFailureRetryAttempts = -1,
                },
                Pooling = new PoolingConfig
                {
                    MaxCachedConnections = 0,
                    MaxConcurrentHttp11HttpsOriginCreates = 0,
                    MaxConcurrentClientConnections = 0,
                    TcpTimeWaitSeconds = -1,
                    ListenerBackLog = 0,
                    ThreadPoolWorkerThread = 0,
                },
                Limits = new LimitsConfig
                {
                    MaxHeaderLineBytes = 0,
                    MaxHeaderCount = 0,
                    MaxHeaderAggregateBytes = 0,
                    MaxEncodedBodyBytes = 0,
                    MaxDecodedBodyBytes = 0,
                    MaxDecompressionRatio = 0,
                    MaxConcurrentClients = 0,
                    MaxConcurrentStreamsPerConnection = 0,
                    MaxPeerInitiatedIncompleteStreamResets = 0,
                    MaxOpenHeaderBlockFrames = 0,
                    MaxOpenHeaderBlockDurationSeconds = 0,
                    MaxCachedConnectionsPerHost = 0,
                    MaxOriginHttp2ConnectionsPerAuthority = 0,
                    MaxCertificateCacheEntries = 0,
                    MaxCertificateDiskCacheEntries = 0,
                    MaxBufferedBodyBytes = -1,
                    MaxDecodedHeaderListBytes = -1,
                    MaxWebSocketFramePayloadBytes = -1,
                },
                PolicyModes = new PolicyModesConfig
                {
                    BodyBudget = "maybe",
                    DecompressionRatio = "maybe",
                    HeaderLimits = "maybe",
                    AdmissionControl = "maybe",
                    Http2AbuseBudget = "maybe",
                },
                Upstream = new UpstreamConfig
                {
                    UpstreamProxyConfigurationScript = "not-a-uri",
                    HttpProxy = new ExternalProxyConfig
                    {
                        HostName = "",
                        Port = 0,
                        ProxyType = "socks9",
                        NextHop = new ExternalProxyConfig { HostName = "", Port = 99999, ProxyType = "ftp" },
                    },
                    HttpsProxy = new ExternalProxyConfig { HostName = "p", Port = 8080, ProxyType = "Http" },
                },
                CertificateManager = new CertificateManagerConfig
                {
                    CertificateEngine = "OpenSsl",
                    LeafCertificateKeyAlgorithm = "Ed25519",
                    CertificateValidDays = 0,
                    CertificateGraceDays = -1,
                    CertificateCacheTimeOutMinutes = 0,
                },
            },
        };

        var errors = TwpConfigValidator.Validate(config);
        Assert.IsTrue(errors.Count >= 20, string.Join('\n', errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Cluster is missing", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("Duplicate cluster", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("unknown cluster", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("Listener type", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("accessLog.path", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("policyModes.bodyBudget", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("certificateEngine", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsAltSvcPortAndMaxAge_AndWarnsWhenHttp3IsOff()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Port = 443,
                    EnableHttp3 = false,
                    AltSvc = new AltSvcConfig { Port = 0, MaxAgeSeconds = 0 },
                },
            ],
        };

        var errors = string.Join('\n', TwpConfigValidator.Validate(config));
        Assert.IsTrue(errors.Contains("altSvc.port", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("altSvc.maxAgeSeconds", StringComparison.Ordinal), errors);
        var warnings = string.Join('\n', TwpConfigValidator.CollectWarnings(config));
        Assert.IsTrue(warnings.Contains("HTTP/3 is off", StringComparison.Ordinal), warnings);
    }

    [TestMethod]
    public void Validate_MasqueLimits_AndWarnsWhenAuthIsOff()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Type = "masque",
                    Host = "0.0.0.0",
                    Port = 443,
                    Masque = new MasqueListenerConfig
                    {
                        RequireAuth = false,
                        AllowedPorts = [0],
                        MaxTunnels = 0,
                        MaxDatagramsPerSecond = 0,
                    },
                },
            ],
        };

        var errors = string.Join('\n', TwpConfigValidator.Validate(config));
        Assert.IsTrue(errors.Contains("allowedPorts", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("maxTunnels", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("maxDatagramsPerSecond", StringComparison.Ordinal), errors);
        var warnings = string.Join('\n', TwpConfigValidator.CollectWarnings(config));
        Assert.IsTrue(warnings.Contains("authentication is off", StringComparison.Ordinal), warnings);
    }

    [TestMethod]
    public void Validate_Masque_RejectsEmptyPorts_AClientCapAboveTheListener_AndHugeRates()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Type = "masque",
                    Port = 443,
                    Masque = new MasqueListenerConfig
                    {
                        RequireAuth = false,
                        AllowedPorts = [],
                        MaxTunnels = 2,
                        MaxTunnelsPerClient = 8,
                        MaxDatagramsPerSecond = 1_000_001,
                        MaxBytesPerSecond = 1_073_741_825,
                    },
                },
            ],
        };

        var errors = string.Join('\n', TwpConfigValidator.Validate(config));
        Assert.IsTrue(errors.Contains("must not be empty", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("must not exceed maxTunnels", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("maxDatagramsPerSecond", StringComparison.Ordinal), errors);
        Assert.IsTrue(errors.Contains("maxBytesPerSecond", StringComparison.Ordinal), errors);
    }

    [TestMethod]
    public void Validate_Masque_RequiresCredentialsUnlessAuthIsOff()
    {
        var missing = TwpConfigValidator.Validate(new TwpConfig
        {
            Listeners = [new ListenerConfig { Type = "masque", Port = 443 }],
        });
        Assert.IsTrue(string.Join('\n', missing).Contains("requires credentials or requireAuth: false", StringComparison.Ordinal));

        var withUsers = TwpConfigValidator.Validate(new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Type = "masque",
                    Port = 443,
                    Masque = new MasqueListenerConfig
                    {
                        Credentials =
                        [
                            new MasqueCredentialConfig { Username = "a", PasswordEnv = "TWP_A" },
                            new MasqueCredentialConfig { Username = "a", PasswordEnv = "TWP_B" },
                            new MasqueCredentialConfig { Username = "", PasswordEnv = "" },
                        ],
                    },
                },
            ],
        });
        var text = string.Join('\n', withUsers);
        Assert.IsFalse(text.Contains("requires credentials", StringComparison.Ordinal), text);
        Assert.IsTrue(text.Contains("duplicated", StringComparison.Ordinal), text);
        Assert.IsTrue(text.Contains("username and passwordEnv", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Validate_NullServer_AndEmptyCollections_AreOk()
    {
        Assert.AreEqual(0, TwpConfigValidator.Validate(new TwpConfig()).Count);
    }
}
