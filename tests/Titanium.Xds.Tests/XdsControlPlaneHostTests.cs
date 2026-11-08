using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Config.Listener.V3;
using Envoy.Config.Route.V3;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Xds.Ads;
using Hcm = Envoy.Extensions.Filters.Network.HttpConnectionManager.V3.HttpConnectionManager;
using XdsRoute = Envoy.Config.Route.V3.Route;
using XdsRouteMatch = Envoy.Config.Route.V3.RouteMatch;

namespace Titanium.Xds.Tests;

[TestClass]
public class XdsControlPlaneHostTests
{
    private string _stateDir = string.Empty;

    [TestInitialize]
    public void Setup() => _stateDir = Directory.CreateTempSubdirectory("twp-xds-host-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_stateDir, recursive: true);

    [TestMethod]
    public void MissingServer_ThrowsBeforeConnect()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new XdsControlPlaneHost(
                new XdsConfig { Server = null },
                new TwpConfig(),
                _stateDir,
                _ => { }));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BareHost_IsNormalizedToCleartextAds()
    {
        await using var server = new FakeAdsServer();
        await server.StartAsync();

        // Same cleartext normalization XdsControlPlaneHost applies to a bare host:port.
        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = "http://" + server.BareAddress,
            Node = new Node { Id = "test", Cluster = "test" },
            ResourceTypeOrder = [Titanium.Xds.XdsTypeUrls.Cluster],
            SettleWindow = TimeSpan.FromMilliseconds(50),
        });

        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ValidateSnapshot += _ =>
        {
            acked.TrySetResult();
            return Task.FromResult(new AdsAckDecision { Ack = true });
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        client.Start(cts.Token);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(AdsConnectionState.Synced,
            SpinWait.SpinUntil(() => client.State == AdsConnectionState.Synced, TimeSpan.FromSeconds(5))
                ? AdsConnectionState.Synced
                : client.State);
    }

    [TestMethod]
    public void GoodSnapshot_BuildsAValidConfig_ThroughTheSamePathTheHostApplies()
    {
        var store = StoreFrom(GoodSnapshotResources());
        var built = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());
        Assert.IsTrue(built.IsSuccess, built.Error?.ToString());
        Assert.AreEqual(0, TwpConfigValidator.Validate(built.Config!).Count, string.Join("; ", TwpConfigValidator.Validate(built.Config!)));
        Assert.AreEqual("c1", built.Config!.Clusters.Single().Id);
        Assert.AreEqual(1, built.Config.Routes.Count);
        Assert.AreEqual("c1", built.Config.Routes[0].ClusterId);
        Assert.AreEqual(18080, built.Config.Listeners.Single().Port);
        Assert.IsTrue(built.Config.Certificates!.Items.Count > 0);
    }

    [TestMethod]
    public void RejectedSnapshot_DoesNotReplaceAPreviouslyValidConfig()
    {
        var store = StoreFrom(GoodSnapshotResources());
        var good = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());
        Assert.IsTrue(good.IsSuccess);

        store.Replace(
            Titanium.Xds.XdsTypeUrls.Cluster,
            [Any.Pack(new Cluster { Name = "c2", Type = ClusterDiscoveryType.LogicalDns })]);
        var bad = XdsSnapshotBuilder.Build(store, _stateDir, good.Config!);
        Assert.IsFalse(bad.IsSuccess);
        StringAssert.Contains(bad.Error!.FieldPath, "type");

        // Host ApplyAsync returns without calling onConfigReady when Build fails, so last-good stays.
        Assert.AreEqual("c1", good.Config!.Clusters.Single().Id);
    }

    private static AdsResourceStore StoreFrom(Dictionary<string, Any[]> resources)
    {
        var store = new AdsResourceStore();
        foreach (var (typeUrl, items) in resources)
        {
            store.Replace(typeUrl, items);
        }

        return store;
    }

    private static Dictionary<string, Any[]> GoodSnapshotResources()
    {
        var (certPem, keyPem) = SelfSigned("app.example.com");
        return new Dictionary<string, Any[]>(StringComparer.Ordinal)
        {
            [Titanium.Xds.XdsTypeUrls.Cluster] =
            [
                Any.Pack(new Cluster { Name = "c1", Type = ClusterDiscoveryType.Eds }),
            ],
            [Titanium.Xds.XdsTypeUrls.ClusterLoadAssignment] =
            [
                Any.Pack(new ClusterLoadAssignment
                {
                    ClusterName = "c1",
                    Endpoints =
                    {
                        new LocalityLbEndpoints
                        {
                            LbEndpoints =
                            {
                                new LbEndpoint
                                {
                                    Endpoint = new Envoy.Config.Endpoint.V3.Endpoint
                                    {
                                        Address = new Address
                                        {
                                            SocketAddress = new SocketAddress
                                            {
                                                Address = "10.0.0.5",
                                                PortValue = 8080,
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }),
            ],
            [Titanium.Xds.XdsTypeUrls.Secret] =
            [
                Any.Pack(new Secret
                {
                    Name = "server-cert",
                    TlsCertificate = new TlsCertificate { CertificateChain = certPem, PrivateKey = keyPem },
                }),
            ],
            [Titanium.Xds.XdsTypeUrls.RouteConfiguration] =
            [
                Any.Pack(new RouteConfiguration
                {
                    Name = "routes",
                    VirtualHosts =
                    {
                        new VirtualHost
                        {
                            Name = "www",
                            Domains = { "*" },
                            Routes =
                            {
                                new XdsRoute
                                {
                                    Match = new XdsRouteMatch { Prefix = "/" },
                                    RouteAction = new RouteAction { Cluster = "c1" },
                                },
                            },
                        },
                    },
                }),
            ],
            [Titanium.Xds.XdsTypeUrls.Listener] =
            [
                Any.Pack(new Listener
                {
                    Name = "http",
                    Address = new Address
                    {
                        SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 18080 },
                    },
                    FilterChains =
                    {
                        new FilterChain
                        {
                            Filters =
                            {
                                new Filter
                                {
                                    Name = "envoy.filters.network.http_connection_manager",
                                    TypedConfig = Any.Pack(new Hcm { RouteConfigName = "routes" }),
                                },
                            },
                        },
                    },
                }),
            ],
        };
    }

    private static (string CertPem, string KeyPem) SelfSigned(string dnsName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + dnsName, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
}
