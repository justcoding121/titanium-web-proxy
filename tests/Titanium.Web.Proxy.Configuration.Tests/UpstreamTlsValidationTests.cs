using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.TestSupport;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>A cluster's upstream TLS files are checked when the configuration loads, not on the first request.</summary>
[TestClass]
public class UpstreamTlsValidationTests
{
    private static string Errors(ClusterHttpClientConfig http) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9, UseHttps = true }],
                    HttpClient = http,
                },
            ],
        }));

    [TestMethod]
    public void GoodPemPairAndCaBundle_AreAccepted()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient();
        var (cert, key) = pki.WritePemPair(client);

        var errors = Errors(new ClusterHttpClientConfig
        {
            ClientCertificatePath = cert,
            ClientCertificateKeyPath = key,
            CaBundlePath = pki.WriteCaPem(),
            ServerName = "origin.test",
            VerifyHostname = false,
        });

        Assert.AreEqual(string.Empty, errors);
    }

    [TestMethod]
    public void GoodPfx_WithPasswordFromEnvironment_IsAccepted()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient();
        var pfx = pki.WritePfx(client, "pfx-secret");
        const string name = "TWP_TEST_VALIDATOR_PFX_PASSWORD";
        Environment.SetEnvironmentVariable(name, "pfx-secret");
        try
        {
            Assert.AreEqual(string.Empty, Errors(new ClusterHttpClientConfig
            {
                ClientCertificatePath = pfx,
                ClientCertificatePasswordEnv = name,
            }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        StringAssert.Contains(
            Errors(new ClusterHttpClientConfig { ClientCertificatePath = pfx, ClientCertificatePasswordEnv = name }),
            "is not set");
    }

    [TestMethod]
    public void MissingFiles_AreReported_WithTheirPath()
    {
        var errors = Errors(new ClusterHttpClientConfig
        {
            ClientCertificatePath = "/nope/client.pem",
            CaBundlePath = "/nope/ca.pem",
        });
        StringAssert.Contains(errors, "clientCertificatePath '/nope/client.pem' was not found");
        StringAssert.Contains(errors, "caBundlePath '/nope/ca.pem' was not found");
    }

    [TestMethod]
    public void KeyWithoutCertificate_AndPasswordWithoutCertificate_AreRejected()
    {
        var errors = Errors(new ClusterHttpClientConfig
        {
            ClientCertificateKeyPath = "client.key",
            ClientCertificatePasswordEnv = "SOME_VAR",
        });
        StringAssert.Contains(errors, "clientCertificateKeyPath requires clientCertificatePath");
        StringAssert.Contains(errors, "clientCertificatePasswordEnv requires clientCertificatePath");
    }

    [TestMethod]
    public void MissingKeyFile_AndMismatchedKey_AreRejected()
    {
        using var pki = new UpstreamTlsTestPki();
        using var one = pki.IssueClient("one");
        using var two = pki.IssueClient("two");
        var (certOne, _) = pki.WritePemPair(one, "one");
        var (_, keyTwo) = pki.WritePemPair(two, "two");

        StringAssert.Contains(
            Errors(new ClusterHttpClientConfig
            {
                ClientCertificatePath = certOne,
                ClientCertificateKeyPath = Path.Combine(pki.Directory, "ghost.key"),
            }),
            "clientCertificateKeyPath");
        StringAssert.Contains(
            Errors(new ClusterHttpClientConfig { ClientCertificatePath = certOne, ClientCertificateKeyPath = keyTwo }),
            "does not parse");
    }

    [TestMethod]
    public void CaBundleWithoutCertificates_AndGarbage_AreRejected()
    {
        using var pki = new UpstreamTlsTestPki();
        var empty = Path.Combine(pki.Directory, "empty.pem");
        File.WriteAllText(empty, "no certificates in here\n");
        var garbage = Path.Combine(pki.Directory, "garbage.crt");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5]);

        StringAssert.Contains(Errors(new ClusterHttpClientConfig { CaBundlePath = empty }), "caBundlePath");
        StringAssert.Contains(Errors(new ClusterHttpClientConfig { CaBundlePath = garbage }), "does not parse");
    }

    [TestMethod]
    public void BlankServerName_IsRejected()
    {
        StringAssert.Contains(Errors(new ClusterHttpClientConfig { ServerName = "  " }), "serverName must not be blank");
    }

    [TestMethod]
    public void Json_MapsTheUpstreamTlsKeys_AndKeepsHostnameVerificationOnByDefault()
    {
        const string json = """
            { "schemaVersion": "7.1",
              "clusters": [ { "id": "c", "destinations": [ { "id": "d", "address": "x", "port": 1 } ],
              "httpClient": {
                "clientCertificatePath": "/etc/c.crt",
                "clientCertificateKeyPath": "/etc/c.key",
                "clientCertificatePasswordEnv": "C_PASSWORD",
                "caBundlePath": "/etc/ca.pem",
                "serverName": "origin.internal" } },
              { "id": "d2", "destinations": [ { "id": "e", "address": "x", "port": 1 } ],
              "httpClient": { "verifyHostname": false } } ] }
            """;
        var config = TwpConfigLoader.LoadJson(json);
        var http = config.Clusters[0].HttpClient!;
        Assert.AreEqual("/etc/c.crt", http.ClientCertificatePath);
        Assert.AreEqual("/etc/c.key", http.ClientCertificateKeyPath);
        Assert.AreEqual("C_PASSWORD", http.ClientCertificatePasswordEnv);
        Assert.AreEqual("/etc/ca.pem", http.CaBundlePath);
        Assert.AreEqual("origin.internal", http.ServerName);
        Assert.IsTrue(http.VerifyHostname);
        Assert.IsFalse(config.Clusters[1].HttpClient!.VerifyHostname);
    }

    [TestMethod]
    public void ClusterWithoutHttpClient_IsUnaffected()
    {
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 }],
                },
            ],
        };
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }
}
