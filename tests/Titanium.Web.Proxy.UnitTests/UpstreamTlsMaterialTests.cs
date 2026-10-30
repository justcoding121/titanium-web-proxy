using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.TestSupport;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Per-cluster upstream TLS: material loading, pool identity, and the central validation policy.
/// </summary>
[TestClass]
public partial class UpstreamTlsMaterialTests
{
    private const string PasswordEnv = "TWP_TEST_UPSTREAM_PFX_PASSWORD";

    private static SessionEventArgs MakeSession(ProxyServer proxy, ClusterHttpClientConfig? httpClient)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts) { UpstreamHttpClient = httpClient };
    }

    [TestMethod]
    public void NoTlsSettings_YieldNoMaterial_AndAnEmptyConnectionKey()
    {
        var plain = new ClusterHttpClientConfig { MaxConnectionsPerServer = 4 };
        Assert.IsNull(UpstreamTlsMaterial.For(null));
        Assert.IsNull(UpstreamTlsMaterial.For(new ClusterHttpClientConfig()));
        Assert.IsNull(UpstreamTlsMaterial.For(plain));
        Assert.AreEqual(string.Empty, new ClusterHttpClientConfig().ConnectionKey);
        StringAssert.DoesNotMatch(plain.ConnectionKey, TlsConnectionKeyMarkerRegex());
    }

    [TestMethod]
    public void AcceptAnyOnly_KeepsTheLegacyKeyShape_WithoutReadingFiles()
    {
        var config = new ClusterHttpClientConfig { AcceptAnyServerCertificate = true };
        var material = UpstreamTlsMaterial.For(config);
        Assert.IsNotNull(material);
        Assert.IsTrue(material.AcceptAny);
        Assert.IsNull(material.Error);
        Assert.AreEqual("|True|||", config.ConnectionKey);
    }

    [TestMethod]
    public void PemPair_LoadsClientCertificate_WithAUsableKey()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient("pem-client");
        var (cert, key) = pki.WritePemPair(client);
        var config = new ClusterHttpClientConfig
        {
            ClientCertificatePath = cert,
            ClientCertificateKeyPath = key,
            CaBundlePath = pki.WriteCaPem(),
            ServerName = " origin.test ",
        };

        var material = UpstreamTlsMaterial.For(config)!;

        Assert.IsNull(material.Error, material.Error);
        Assert.IsTrue(material.ClientCertificate!.HasPrivateKey);
        Assert.AreEqual(client.Thumbprint, material.ClientCertificate.Thumbprint);
        Assert.AreEqual(1, material.Roots!.Count);
        Assert.AreEqual("origin.test", material.ServerName);
        Assert.AreSame(material, UpstreamTlsMaterial.For(config), "material is built once per config instance");
    }

    [TestMethod]
    public void EncryptedPemKey_UsesThePasswordFromTheNamedVariable()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient();
        var (cert, key) = pki.WriteEncryptedPemPair(client, "s3cret-pass");
        Environment.SetEnvironmentVariable(PasswordEnv, "s3cret-pass");
        try
        {
            var material = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
            {
                ClientCertificatePath = cert,
                ClientCertificateKeyPath = key,
                ClientCertificatePasswordEnv = PasswordEnv,
            })!;
            Assert.IsNull(material.Error, material.Error);
            Assert.IsTrue(material.ClientCertificate!.HasPrivateKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordEnv, null);
        }
    }

    [TestMethod]
    public void Pfx_LoadsWithPasswordFromEnvironment_AndFailsClosedWhenTheVariableIsMissing()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient();
        var pfx = pki.WritePfx(client, "pfx-pass");
        Environment.SetEnvironmentVariable(PasswordEnv, "pfx-pass");
        try
        {
            var ok = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
            {
                ClientCertificatePath = pfx,
                ClientCertificatePasswordEnv = PasswordEnv,
            })!;
            Assert.IsNull(ok.Error, ok.Error);
            Assert.IsTrue(ok.ClientCertificate!.HasPrivateKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordEnv, null);
        }

        var unset = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
        {
            ClientCertificatePath = pfx,
            ClientCertificatePasswordEnv = PasswordEnv,
        })!;
        StringAssert.Contains(unset.Error, PasswordEnv);
        Assert.IsNull(unset.ClientCertificate);
        Assert.ThrowsExactly<AuthenticationException>(unset.ThrowIfFaulted);
    }

    [TestMethod]
    public void MissingFile_FailsClosed_AndNamesTheSetting_NeverTheSecret()
    {
        var material = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
        {
            ClientCertificatePath = Path.Combine(Path.GetTempPath(), "twp-does-not-exist.pem"),
            ClientCertificateKeyPath = Path.Combine(Path.GetTempPath(), "twp-does-not-exist.key"),
        })!;
        StringAssert.Contains(material.Error, "clientCertificatePath");
        Assert.IsNull(material.ClientCertificate);
        var thrown = Assert.ThrowsExactly<AuthenticationException>(material.ThrowIfFaulted);
        StringAssert.Contains(thrown.Message, "unusable");
        Assert.IsTrue(material.TryClaimErrorLog(), "the first claim logs");
        Assert.IsFalse(material.TryClaimErrorLog(), "later claims stay quiet");
    }

    [TestMethod]
    public void KeyThatDoesNotMatchTheCertificate_FailsClosed()
    {
        using var pki = new UpstreamTlsTestPki();
        using var one = pki.IssueClient("one");
        using var two = pki.IssueClient("two");
        var (certOne, _) = pki.WritePemPair(one, "one");
        var (_, keyTwo) = pki.WritePemPair(two, "two");

        var material = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
        {
            ClientCertificatePath = certOne,
            ClientCertificateKeyPath = keyTwo,
        })!;

        Assert.IsNotNull(material.Error);
        Assert.IsNull(material.ClientCertificate);
    }

    [TestMethod]
    public void GarbageKeyFile_AndEmptyCaBundle_FailClosed()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient();
        var (cert, _) = pki.WritePemPair(client);
        var badKey = Path.Combine(pki.Directory, "bad.key");
        File.WriteAllText(badKey, "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n");
        var badKeyMaterial = UpstreamTlsMaterial.For(new ClusterHttpClientConfig
        {
            ClientCertificatePath = cert,
            ClientCertificateKeyPath = badKey,
        })!;
        Assert.IsNotNull(badKeyMaterial.Error);

        var emptyBundle = Path.Combine(pki.Directory, "empty.pem");
        File.WriteAllText(emptyBundle, "# no certificates here\n");
        var emptyMaterial = UpstreamTlsMaterial.For(new ClusterHttpClientConfig { CaBundlePath = emptyBundle })!;
        Assert.IsNotNull(emptyMaterial.Error);
        Assert.IsNull(emptyMaterial.Roots);
    }

    [TestMethod]
    public void Fingerprint_FollowsFileContent_AndConnectionKeyFollowsFingerprint()
    {
        using var pki = new UpstreamTlsTestPki();
        using var other = new UpstreamTlsTestPki("Another Test CA");
        var caPath = pki.WriteCaPem();

        var first = new ClusterHttpClientConfig { CaBundlePath = caPath };
        var again = new ClusterHttpClientConfig { CaBundlePath = caPath };
        var firstKey = first.ConnectionKey;
        Assert.AreEqual(firstKey, again.ConnectionKey, "same content, same pool identity");
        StringAssert.Contains(firstKey, "|tls:");
        Assert.AreEqual(64, UpstreamTlsMaterial.For(first)!.Fingerprint.Length);

        // The same path with rotated content is a different identity once the config is rebuilt.
        File.WriteAllText(caPath, other.CaCertificate.ExportCertificatePem());
        var rotated = new ClusterHttpClientConfig { CaBundlePath = caPath };
        Assert.AreNotEqual(firstKey, rotated.ConnectionKey);
        Assert.AreEqual(firstKey, first.ConnectionKey, "the key is computed once and never re-reads the file");

        Assert.AreNotEqual(
            new ClusterHttpClientConfig { ServerName = "a.test" }.ConnectionKey,
            new ClusterHttpClientConfig { ServerName = "b.test" }.ConnectionKey);
        Assert.AreNotEqual(
            new ClusterHttpClientConfig { VerifyHostname = false }.ConnectionKey,
            new ClusterHttpClientConfig { ServerName = "b.test" }.ConnectionKey);
        Assert.AreNotEqual(string.Empty, new ClusterHttpClientConfig { VerifyHostname = false }.ConnectionKey);
    }

    [TestMethod]
    public void ApplyPolicy_MasksOnlyANameMismatch_WhenHostnameVerificationIsOff()
    {
        using var pki = new UpstreamTlsTestPki();
        using var server = pki.IssueServer("origin.test");
        var strict = UpstreamTlsMaterial.For(new ClusterHttpClientConfig { ServerName = "origin.test" })!;
        var lenient = UpstreamTlsMaterial.For(new ClusterHttpClientConfig { VerifyHostname = false })!;

        const SslPolicyErrors both = SslPolicyErrors.RemoteCertificateNameMismatch |
                                     SslPolicyErrors.RemoteCertificateChainErrors;
        Assert.AreEqual(both, strict.ApplyPolicy(server, null, both, X509RevocationMode.NoCheck));
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors,
            lenient.ApplyPolicy(server, null, both, X509RevocationMode.NoCheck));
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateNotAvailable,
            lenient.ApplyPolicy(null, null, SslPolicyErrors.RemoteCertificateNotAvailable,
                X509RevocationMode.NoCheck));
    }

    [TestMethod]
    public void ApplyPolicy_RebuildsTheChainAgainstTheClusterRoots_Only()
    {
        using var pki = new UpstreamTlsTestPki();
        using var stranger = new UpstreamTlsTestPki("Stranger CA");
        using var server = pki.IssueServer("origin.test");
        var trusting = UpstreamTlsMaterial.For(new ClusterHttpClientConfig { CaBundlePath = pki.WriteCaPem() })!;
        var wrong = UpstreamTlsMaterial.For(new ClusterHttpClientConfig { CaBundlePath = stranger.WriteCaPem() })!;

        // The platform said "untrusted root"; the cluster's own anchors overrule that verdict.
        Assert.AreEqual(SslPolicyErrors.None,
            trusting.ApplyPolicy(server, null, SslPolicyErrors.RemoteCertificateChainErrors,
                X509RevocationMode.NoCheck));
        // And the reverse: a platform "fine" is not enough when the cluster's anchors do not vouch for it.
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors,
            wrong.ApplyPolicy(server, null, SslPolicyErrors.None, X509RevocationMode.NoCheck));
    }

    [TestMethod]
    public void ValidateServerCertificate_HonorsClusterPolicy_AndTheUserCallbackStillSeesWhatIsLeft()
    {
        using var pki = new UpstreamTlsTestPki();
        using var server = pki.IssueServer("origin.test");
        using var proxy = new ProxyServer(false, false, false);

        // No cluster settings: unchanged default, a chain error is refused.
        using var plain = MakeSession(proxy, null);
        Assert.IsFalse(proxy.ValidateServerCertificate(proxy, plain, server, null,
            SslPolicyErrors.RemoteCertificateChainErrors));

        // AcceptAny short-circuits for any transport that routes through the central check (TCP and QUIC).
        using var acceptAny = MakeSession(proxy, new ClusterHttpClientConfig { AcceptAnyServerCertificate = true });
        Assert.IsTrue(proxy.ValidateServerCertificate(proxy, acceptAny, server, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));

        // A custom CA clears the chain error; the name still has to match.
        using var anchored = MakeSession(proxy, new ClusterHttpClientConfig { CaBundlePath = pki.WriteCaPem() });
        Assert.IsTrue(proxy.ValidateServerCertificate(proxy, anchored, server, null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsFalse(proxy.ValidateServerCertificate(proxy, anchored, server, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));

        // verifyHostname:false lets a name mismatch pass, but not a chain error.
        using var lenient = MakeSession(proxy, new ClusterHttpClientConfig { VerifyHostname = false });
        Assert.IsTrue(proxy.ValidateServerCertificate(proxy, lenient, server, null,
            SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.IsFalse(proxy.ValidateServerCertificate(proxy, lenient, server, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));

        // An unusable file never validates, even with acceptAny.
        using var broken = MakeSession(proxy, new ClusterHttpClientConfig
        {
            AcceptAnyServerCertificate = true,
            CaBundlePath = Path.Combine(pki.Directory, "missing.pem"),
        });
        Assert.IsFalse(proxy.ValidateServerCertificate(proxy, broken, server, null, SslPolicyErrors.None));
    }

    [TestMethod]
    public void SelectClientCertificate_PrefersTheClusterIdentity_AndTheUserCallbackCanStillOverride()
    {
        using var pki = new UpstreamTlsTestPki();
        using var client = pki.IssueClient("cluster-client");
        using var fromStore = pki.IssueClient("store-client");
        var (cert, key) = pki.WritePemPair(client);
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy, new ClusterHttpClientConfig
        {
            ClientCertificatePath = cert,
            ClientCertificateKeyPath = key,
        });
        var local = new X509CertificateCollection { fromStore };

        var chosen = proxy.SelectClientCertificate(proxy, session, "origin.test", local, null, null);
        Assert.AreEqual(client.Thumbprint, ((X509Certificate2)chosen!).Thumbprint);

        using var plain = MakeSession(proxy, null);
        var fallback = proxy.SelectClientCertificate(proxy, plain, "origin.test", local, null, null);
        Assert.AreEqual(fromStore.Thumbprint, ((X509Certificate2)fallback!).Thumbprint);

        proxy.ClientCertificateSelectionCallback += (_, args) =>
        {
            args.ClientCertificate = fromStore;
            return Task.CompletedTask;
        };
        var overridden = proxy.SelectClientCertificate(proxy, session, "origin.test", local, null, null);
        Assert.AreEqual(fromStore.Thumbprint, ((X509Certificate2)overridden!).Thumbprint);
    }

    [TestMethod]
    public void QuicCacheKey_SeparatesTlsIdentities_AndStaysUnchangedWithoutMaterial()
    {
        var plain = Network.Quic.QuicConnectionFactory.GetCacheKey("h", 443, "sni", null, null);
        Assert.AreEqual(plain, Network.Quic.QuicConnectionFactory.GetCacheKey("h", 443, "sni", null, null, null));
        var a = Network.Quic.QuicConnectionFactory.GetCacheKey("h", 443, "sni", null, null, "aa");
        var b = Network.Quic.QuicConnectionFactory.GetCacheKey("h", 443, "sni", null, null, "bb");
        Assert.AreNotEqual(plain, a);
        Assert.AreNotEqual(a, b);
    }

    /// <summary>
    ///     Documented choice: a mirror copy ignores the cluster's <c>httpClient</c> TLS options and uses the
    ///     machine trust store, so it never widens what the proxy trusts. A private-CA origin fails the copy.
    /// </summary>
    [TestMethod]
    public async Task Mirror_IgnoresClusterTlsOptions_SoAPrivateCaOriginFailsTheBestEffortCopy()
    {
        using var pki = new UpstreamTlsTestPki();
        using var server = pki.IssueServer("localhost");
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var fanout = new Titanium.Web.Proxy.Routing.MirrorDispatcher();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream(), false);
                try
                {
                    await ssl.AuthenticateAsServerAsync(server);
                }
                catch (Exception)
                {
                    // The mirror's client rejects the private-CA certificate; that is the expected outcome.
                }
            });

            var request = new Titanium.Web.Proxy.Http.Request { Method = "GET", Host = "app.test" };
            request.RequestUriString8 = (Titanium.Web.Proxy.Models.ByteString)"/m";
            fanout.TryEnqueue(
                new Abstractions.Routing.MirrorConfig { ClusterId = "m" },
                request,
                new DestinationConfig { Id = "d", Address = "localhost", Port = port, UseHttps = true });

            var until = DateTime.UtcNow.AddSeconds(10);
            while (fanout.FailedForTests == 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(20);
            }

            Assert.AreEqual(1, fanout.FailedForTests);
            Assert.AreEqual(0, fanout.SentForTests);
        }
        finally
        {
            listener.Stop();
        }
    }

    [GeneratedRegex("tls:")]
    private static partial Regex TlsConnectionKeyMarkerRegex();
}