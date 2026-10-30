using System.Net.Security;
using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>Listener TLS policy (<c>server.tls</c>) and the per-cluster PROXY protocol setting.</summary>
[TestClass]
public class ListenerTlsPolicyConfigTests
{
    private static readonly string[] Tls13CipherSuites = ["TLS_AES_256_GCM_SHA384"];

    private static TwpConfig WithTls(TlsConfig tls) => new() { Server = new ServerConfig { Tls = tls } };

    private static string Errors(TwpConfig config) => string.Join("\n", TwpConfigValidator.Validate(config));

    private static ClusterConfig Cluster(string id, string? sendProxyProtocol) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = 9 }],
        SendProxyProtocol = sendProxyProtocol,
    };

    [TestMethod]
    public void Unset_ValidatesAndWarnsNothing()
    {
        var config = WithTls(new TlsConfig());

        Assert.AreEqual(string.Empty, Errors(config));
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config, isWindows: true).Count);
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config, isWindows: false).Count);
    }

    [TestMethod]
    [DataRow("Tls12")]
    [DataRow("tls13")]
    [DataRow("1.2")]
    [DataRow("TLSv1.3")]
    public void MinVersion_AcceptsTls12AndTls13Spellings(string value)
    {
        Assert.AreEqual(string.Empty, Errors(WithTls(new TlsConfig { MinVersion = value })));
        Assert.IsTrue(TlsPolicyParser.TryParseMinVersion(value, out _));
    }

    [TestMethod]
    [DataRow("Tls11")]
    [DataRow("Tls")]
    [DataRow("1.1")]
    [DataRow("")]
    [DataRow("Tls14")]
    [DataRow("Ssl3")]
    public void MinVersion_RejectsOlderOrUnknownVersions(string value)
    {
        StringAssert.Contains(Errors(WithTls(new TlsConfig { MinVersion = value })), "server.tls.minVersion");
    }

    [TestMethod]
    public void MinVersion_ParsesToTheProtocolFlag()
    {
        Assert.IsTrue(TlsPolicyParser.TryParseMinVersion("Tls13", out var minimum));
        Assert.AreEqual(SslProtocols.Tls13, minimum);
        Assert.IsTrue(TlsPolicyParser.TryParseMinVersion("1.2", out minimum));
        Assert.AreEqual(SslProtocols.Tls12, minimum);
    }

    [TestMethod]
    public void MinVersion_ContradictingTheSupportedList_IsRejected()
    {
        var config = WithTls(new TlsConfig { MinVersion = "Tls13", SupportedSslProtocols = ["Tls12"] });

        StringAssert.Contains(Errors(config), "excludes every protocol");

        config.Server!.Tls!.SupportedSslProtocols = ["Tls12", "Tls13"];
        Assert.AreEqual(string.Empty, Errors(config));

        config.Server.Tls.SupportedSslProtocols = ["None"];
        Assert.AreEqual(string.Empty, Errors(config), "None means the default list, which includes Tls13");
    }

    [TestMethod]
    public void CipherSuites_AcceptKnownNames_AndRejectUnknownOrNumeric()
    {
        Assert.AreEqual(string.Empty, Errors(WithTls(new TlsConfig
        {
            CipherSuites = ["TLS_AES_256_GCM_SHA384", "tls_ecdhe_rsa_with_aes_128_gcm_sha256"],
        })));

        var errors = Errors(WithTls(new TlsConfig { CipherSuites = ["TLS_NOPE", "4865", ""] }));
        StringAssert.Contains(errors, "'TLS_NOPE'");
        StringAssert.Contains(errors, "'4865'");
        Assert.AreEqual(3, errors.Split('\n').Length);
    }

    [TestMethod]
    public void CipherSuites_EmptyList_IsRejected()
    {
        StringAssert.Contains(Errors(WithTls(new TlsConfig { CipherSuites = [] })), "at least one suite");
    }

    [TestMethod]
    public void CipherSuites_ParseDeduplicatedInOrder()
    {
        Assert.IsTrue(TlsPolicyParser.TryParseCipherSuites(
            ["TLS_AES_256_GCM_SHA384", "TLS_AES_128_GCM_SHA256", "tls_aes_256_gcm_sha384"], out var suites));
        CollectionAssert.AreEqual(
            new[] { TlsCipherSuite.TLS_AES_256_GCM_SHA384, TlsCipherSuite.TLS_AES_128_GCM_SHA256 },
            suites.ToArray());
        Assert.IsFalse(TlsPolicyParser.TryParseCipherSuites(["TLS_AES_256_GCM_SHA384", "bogus"], out _));
    }

    [TestMethod]
    public void CipherSuites_WarnOnWindowsOnly()
    {
        var config = WithTls(new TlsConfig { CipherSuites = ["TLS_AES_256_GCM_SHA384"] });

        var windows = string.Join("\n", TwpConfigValidator.CollectWarnings(config, isWindows: true));
        StringAssert.Contains(windows, "server.tls.cipherSuites is ignored on Windows");
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config, isWindows: false).Count);
        Assert.AreEqual(string.Empty, Errors(config), "a warning, not a failure");
    }

    [TestMethod]
    public void Documents_ReadTlsPolicyAndClusterProxyProtocol()
    {
        var yaml = TwpConfigLoader.LoadYaml("""
            server:
              tls:
                minVersion: Tls13
                cipherSuites: [TLS_AES_256_GCM_SHA384]
                ocspStapling: true
            """);
        var json = TwpConfigLoader.LoadJson("""
            {
              "clusters": [
                {
                  "id": "tcp",
                  "sendProxyProtocol": "v2",
                  "destinations": [{ "id": "o1", "address": "127.0.0.1", "port": 9 }]
                }
              ]
            }
            """);

        Assert.AreEqual("Tls13", yaml.Server!.Tls!.MinVersion);
        CollectionAssert.AreEqual(Tls13CipherSuites, yaml.Server.Tls.CipherSuites!.ToArray());
        Assert.IsTrue(yaml.Server.Tls.OcspStapling);
        Assert.AreEqual(string.Empty, Errors(yaml));
        Assert.AreEqual("v2", json.Clusters[0].SendProxyProtocol);
        Assert.AreEqual(string.Empty, Errors(json));
    }
    [TestMethod]
    public void ClusterSendProxyProtocol_ValidatesNoneAndV2Only()
    {
        Assert.AreEqual(string.Empty, Errors(new TwpConfig { Clusters = [Cluster("a", null), Cluster("b", "none"), Cluster("c", "V2")] }));

        StringAssert.Contains(
            Errors(new TwpConfig { Clusters = [Cluster("a", "v1")] }),
            "Cluster 'a' sendProxyProtocol 'v1' is not none or v2.");
    }

    [TestMethod]
    public void ClusterSendProxyProtocolV2_WarnsWhenNoPassthroughListenerUsesTheCluster()
    {
        var config = new TwpConfig { Clusters = [Cluster("web", "v2")] };

        StringAssert.Contains(
            string.Join("\n", TwpConfigValidator.CollectWarnings(config, isWindows: false)),
            "Cluster 'web' sets sendProxyProtocol v2");

        config.Listeners =
        [
            new ListenerConfig
            {
                Port = 443,
                Passthrough = true,
                DecryptSsl = false,
                SniDefaultClusterId = "web",
            },
        ];
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config, isWindows: false).Count);
    }
}
