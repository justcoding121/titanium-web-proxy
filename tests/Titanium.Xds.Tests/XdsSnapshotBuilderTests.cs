using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Xds.Ads;

namespace Titanium.Xds.Tests;

[TestClass]
public class XdsSnapshotBuilderTests
{
    private const string AssignmentUrl = "type.googleapis.com/envoy.config.endpoint.v3.ClusterLoadAssignment";
    private const string SecretUrl = "type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.Secret";

    private string _stateDir = string.Empty;

    [TestInitialize]
    public void Setup() => _stateDir = Directory.CreateTempSubdirectory("twp-xds-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_stateDir, recursive: true);

    private static ClusterLoadAssignment Assignment(string cluster) => new()
    {
        ClusterName = cluster,
        Endpoints =
        {
            new Envoy.Config.Endpoint.V3.LocalityLbEndpoints
            {
                LbEndpoints =
                {
                    new Envoy.Config.Endpoint.V3.LbEndpoint
                    {
                        Endpoint = new Envoy.Config.Endpoint.V3.Endpoint
                        {
                            Address = new Address
                            {
                                SocketAddress = new SocketAddress { Address = "10.0.0.5", PortValue = 8080 },
                            },
                        },
                    },
                },
            },
        },
    };

    [TestMethod]
    public void Eds_KeepsTheClusterSettingsFromCds()
    {
        var store = new AdsResourceStore();
        store.Replace(XdsTypeUrls.Cluster,
        [
            Any.Pack(new Cluster
            {
                Name = "c1",
                Type = ClusterDiscoveryType.Eds,
                LbPolicy = LbPolicy.LeastRequest,
                ConnectTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(7)),
            }),
        ]);
        store.Replace(AssignmentUrl, [Any.Pack(Assignment("c1"))]);

        var result = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var cluster = result.Config!.Clusters.Single();
        Assert.AreEqual(1, cluster.Destinations.Count);
        Assert.AreEqual("10.0.0.5", cluster.Destinations[0].Address);
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, cluster.Algorithm);
        Assert.AreEqual(TimeSpan.FromSeconds(7), cluster.HttpClient!.ConnectTimeout);
    }

    private static readonly string[] ClusterConfigPropertyNames =
    [
        "AffinityCookie", "AffinityHeader", "Algorithm", "Destinations", "HashKey", "HealthCheck", "HttpClient",
        "Id", "LoadBalancingPolicy", "Queue", "Retry", "SendProxyProtocol", "SessionAffinity", "SlowStartSeconds",
        "ZoneAware",
    ];

    private static readonly string[] AppExampleHost = ["app.example.com"];

    [TestMethod]
    public void ClusterConfig_PropertySet_IsCoveredByTheEdsCopy()
    {
        // XdsSnapshotBuilder.WithDestinations copies every ClusterConfig setting. A new property must be added there
        // too, otherwise EDS silently drops it; this list fails first.
        var names = typeof(ClusterConfig).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        CollectionAssert.AreEqual(ClusterConfigPropertyNames, names.ToArray());
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

    [TestMethod]
    public void TlsCertificateSecret_BecomesAnSniCertificateItem_AndKeepsFileItems()
    {
        var (certPem, keyPem) = SelfSigned("app.example.com");
        var store = new AdsResourceStore();
        store.Replace(SecretUrl,
        [
            Any.Pack(new Secret
            {
                Name = "server-cert",
                TlsCertificate = new TlsCertificate { CertificateChain = certPem, PrivateKey = keyPem },
            }),
        ]);
        var bootstrap = new TwpConfig
        {
            Certificates = new CertificatesConfig
            {
                AcmeEmail = "ops@example.com",
                Items = { new CertificateFileConfig { Path = "file.pfx", Hosts = { "file.example.com" } } },
            },
        };

        var result = XdsSnapshotBuilder.Build(store, _stateDir, bootstrap);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var certificates = result.Config!.Certificates!;
        Assert.AreEqual("ops@example.com", certificates.AcmeEmail);
        Assert.AreEqual(2, certificates.Items.Count);
        Assert.AreEqual("file.pfx", certificates.Items[0].Path);
        var sds = certificates.Items[1];
        Assert.IsTrue(File.Exists(sds.Path));
        CollectionAssert.AreEqual(AppExampleHost, sds.Hosts.ToArray());
        using var loaded = X509CertificateLoader.LoadPkcs12FromFile(sds.Path!, password: null);
        Assert.IsTrue(loaded.HasPrivateKey);
    }

    [TestMethod]
    public void MismatchedCertificateAndKey_IsRejected()
    {
        var (certPem, _) = SelfSigned("a.example.com");
        var (_, otherKeyPem) = SelfSigned("b.example.com");
        var store = new AdsResourceStore();
        store.Replace(SecretUrl,
        [
            Any.Pack(new Secret
            {
                Name = "bad",
                TlsCertificate = new TlsCertificate { CertificateChain = certPem, PrivateKey = otherKeyPem },
            }),
        ]);

        var result = XdsSnapshotBuilder.Build(store, _stateDir, new TwpConfig());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("bad", result.Error!.ResourceName);
    }

    [TestMethod]
    public void NoSecrets_LeavesBootstrapCertificatesUntouched()
    {
        var bootstrap = new TwpConfig { Certificates = new CertificatesConfig { AcmeEmail = "ops@example.com" } };

        var result = XdsSnapshotBuilder.Build(new AdsResourceStore(), _stateDir, bootstrap);

        Assert.AreSame(bootstrap.Certificates, result.Config!.Certificates);
    }
}
