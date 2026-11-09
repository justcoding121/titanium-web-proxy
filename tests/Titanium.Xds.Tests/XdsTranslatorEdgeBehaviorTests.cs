using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Config.Cluster.V3;
using Envoy.Config.Route.V3;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Ads;
using Titanium.Xds.Translation;
using XdsRoute = Envoy.Config.Route.V3.Route;

namespace Titanium.Xds.Tests;

/// <summary>
///     Thin translator / store edges: unknown ADS type URLs and routes with no action.
/// </summary>
[TestClass]
public class XdsTranslatorEdgeBehaviorTests
{
    private string _stateDir = string.Empty;

    [TestInitialize]
    public void Setup() => _stateDir = Directory.CreateTempSubdirectory("twp-xds-edge-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_stateDir, recursive: true);

    [TestMethod]
    public void AdsResourceStore_UnknownTypeUrl_IndexesByAnyTypeUrl()
    {
        // Bug class: unknown typed resources collide under one key and overwrite each other.
        var store = new AdsResourceStore();
        var first = Any.Pack(new Secret { Name = "a" });
        first.TypeUrl = "type.googleapis.com/example.Unknown";
        var second = Any.Pack(new Secret { Name = "b" });
        second.TypeUrl = "type.googleapis.com/example.Other";

        store.Replace("type.googleapis.com/example.Custom", [first, second]);

        Assert.IsTrue(store.TryGetAll("type.googleapis.com/example.Custom", out var map));
        Assert.AreEqual(2, map.Count);
        Assert.IsTrue(map.ContainsKey("type.googleapis.com/example.Unknown"));
        Assert.IsTrue(map.ContainsKey("type.googleapis.com/example.Other"));
    }

    [TestMethod]
    public void RouteTranslator_MissingAction_Nacks()
    {
        var configuration = new RouteConfiguration
        {
            Name = "r1",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "vh",
                    Domains = { "*" },
                    Routes =
                    {
                        new XdsRoute
                        {
                            Match = new RouteMatch { Prefix = "/" },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.Message, "unsupported route action");
        StringAssert.Contains(result.Error.FieldPath, "action");
    }

    [TestMethod]
    public void ListenerTranslator_MissingSocketAddress_Nacks()
    {
        var result = ListenerTranslator.Translate(new Envoy.Config.Listener.V3.Listener { Name = "no-addr" });
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("address.socket_address", result.Error!.FieldPath);
        StringAssert.Contains(result.Error.Message, "listener address is required");
    }

    [TestMethod]
    public void ClusterTranslator_ZeroConnectTimeout_StillBuildsHttpClient()
    {
        // Bug class: ConnectTimeout of 0 drops HttpClient and origins hang forever.
        var result = ClusterTranslator.Translate(new Cluster
        {
            Name = "c-zero",
            Type = ClusterDiscoveryType.Eds,
            ConnectTimeout = Duration.FromTimeSpan(TimeSpan.Zero),
        });
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value!.HttpClient);
        Assert.AreEqual(TimeSpan.Zero, result.Value.HttpClient!.ConnectTimeout);
    }

    [TestMethod]
    public void SecretTranslator_SanitizesUnsafeSecretNames_InPaths()
    {
        // Bug class: secret names with path separators escape the state directory.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=safe.example", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("safe.example");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var secret = new Secret
        {
            Name = "../bad/name",
            TlsCertificate = new TlsCertificate
            {
                CertificateChain = cert.ExportCertificatePem(),
                PrivateKey = key.ExportPkcs8PrivateKeyPem(),
            },
        };

        var result = SecretTranslator.Translate(secret, _stateDir);
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        var certPath = result.Value!.CertificateChainPath!;
        Assert.AreEqual(_stateDir, Path.GetDirectoryName(certPath));
        Assert.AreEqual(".._bad_name.cert.pem", Path.GetFileName(certPath));
        Assert.IsTrue(File.Exists(certPath));
    }
}
