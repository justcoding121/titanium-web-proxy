using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Config.Route.V3;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Envoy.Service.Discovery.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Xds.Ads;
using Titanium.Xds.Translation;
using XdsRoute = Envoy.Config.Route.V3.Route;
using XdsRouteMatch = Envoy.Config.Route.V3.RouteMatch;

namespace Titanium.Xds.Tests;

[TestClass]
public class XdsSnapshotGapBehaviorTests
{
    private static readonly string[] ExpectedWildcardHosts = ["*"];
    private string _stateDir = string.Empty;

    [TestInitialize]
    public void Setup() => _stateDir = Directory.CreateTempSubdirectory("twp-xds-gap-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_stateDir, recursive: true);

    [TestMethod]
    public void EndpointAssignmentWithoutPort_NacksWithTheClusterName()
    {
        var store = new AdsResourceStore();
        store.Replace(XdsTypeUrls.Cluster, [Any.Pack(new Cluster { Name = "c1", Type = ClusterDiscoveryType.Eds })]);
        store.Replace(XdsTypeUrls.ClusterLoadAssignment,
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
                                    Address = new Address { SocketAddress = new SocketAddress { Address = "10.0.0.5" } },
                                },
                            },
                        },
                    },
                },
            }),
        ]);

        var result = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("c1", result.Error!.ResourceName);
        StringAssert.Contains(result.Error.FieldPath, "port_value");
    }

    [TestMethod]
    public void EndpointAssignmentForUnknownCluster_Nacks()
    {
        // Bug class: endpoints for a cluster CDS never defined are silently dropped and the snapshot is ACK'd.
        var store = new AdsResourceStore();
        store.Replace(XdsTypeUrls.Cluster, [Any.Pack(new Cluster { Name = "c1", Type = ClusterDiscoveryType.Eds })]);
        store.Replace(XdsTypeUrls.ClusterLoadAssignment,
        [
            Any.Pack(new ClusterLoadAssignment
            {
                ClusterName = "ghost",
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
                                        SocketAddress = new SocketAddress { Address = "10.0.0.5", PortValue = 80 },
                                    },
                                },
                            },
                        },
                    },
                },
            }),
        ]);

        var result = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("ghost", result.Error!.ResourceName);
        StringAssert.Contains(result.Error.Message, "unknown cluster");
    }

    [TestMethod]
    public void RouteWithoutAnAction_NacksWithTheActionFieldPath()
    {
        var result = RouteTranslator.Translate(new RouteConfiguration
        {
            Name = "routes",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "www",
                    Domains = { "*" },
                    Routes = { new XdsRoute { Match = new XdsRouteMatch { Prefix = "/" } } },
                },
            },
        });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "action");
        Assert.AreEqual("routes", result.Error.ResourceName);
    }

    [TestMethod]
    public void CertificateWithoutSanOrSubject_BindsEveryServerName()
    {
        // Bug class: a certificate with no usable name is dropped instead of becoming the wildcard SNI fallback.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(string.Empty), key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var secret = new Secret
        {
            Name = "anonymous",
            TlsCertificate = new TlsCertificate
            {
                CertificateChain = cert.ExportCertificatePem(),
                PrivateKey = key.ExportPkcs8PrivateKeyPem(),
            },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        CollectionAssert.AreEqual(ExpectedWildcardHosts, result.Value!.Hosts.ToArray());
    }

    [TestMethod]
    public void SecretNameWithPathCharacters_StaysInsideTheStateDirectory()
    {
        // Bug class: a secret named "../x" escapes the state directory.
        var secret = new Secret
        {
            Name = "..\\..\\evil/ca",
            ValidationContext = new CertificateValidationContext { TrustedCa = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n" },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var written = Path.GetFullPath(result.Value!.TrustedCaPath!);
        Assert.AreEqual(Path.GetFullPath(_stateDir), Path.GetDirectoryName(written));
    }

    [TestMethod]
    public void ResourceOfAnUnknownType_IsStoredUnderItsTypeUrl()
    {
        var store = new AdsResourceStore();
        var unknown = Any.Pack(new Duration { Seconds = 3 });

        store.Replace("type.example/unknown", [unknown]);

        Assert.IsTrue(store.TryGetAll("type.example/unknown", out var all));
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(unknown.TypeUrl, all.Keys.Single());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ControlPlaneThatHangsUpAtOnce_IsNotRedialedInATightLoop()
    {
        // Bug class: a clean stream end skipped the backoff, so a server closing every stream was hammered.
        var state = new FakeAdsState { CloseStreamsImmediately = true };
        await using var server = new FakeAdsServer(state);
        await server.StartAsync();
        await using var client = new AdsClient(new AdsClientOptions
        {
            ServerAddress = server.Address,
            Node = new Node { Id = "hangup", Cluster = "test" },
            ResourceTypeOrder = [XdsTypeUrls.Cluster],
            ReconnectDelay = TimeSpan.FromMilliseconds(400),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        client.Start(cts.Token);
        await Task.Delay(1500);

        Assert.IsTrue(state.StreamCount >= 2, $"the client should reconnect; streams={state.StreamCount}");
        Assert.IsTrue(state.StreamCount <= 6, $"reconnects must be paced; streams={state.StreamCount}");
        Assert.AreNotEqual(AdsConnectionState.Synced, client.State);
    }
}
