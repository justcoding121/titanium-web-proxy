using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression-2026-10-07: several Titanium roots with the same CN accumulate in the trust store. New roots
///     carry a Subject Key Identifier and leaves signed by them an Authority Key Identifier, so chain builders
///     can pick the right root; leaves under a legacy root without an SKI stay unchanged (no AKI).
/// </summary>
[TestClass]
[TestCategory("Regression-2026-10-07")]
public class CertificateKeyIdentifierTests
{
    private const string SkiOid = "2.5.29.14";
    private const string AkiOid = "2.5.29.35";

    [TestMethod]
    public void NewRoot_HasSki_LeafHasMatchingAki_ChainBuilds()
    {
        foreach (var engine in new[] { CertificateEngine.BouncyCastle, CertificateEngine.BouncyCastleFast })
        foreach (var algorithm in new[] { CertificateKeyAlgorithm.Rsa2048, CertificateKeyAlgorithm.EcdsaP256 })
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = engine,
                LeafCertificateKeyAlgorithm = algorithm,
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            var root = mgr.RootCertificate!;
            using var leaf = mgr.CreateCertificate("ski-test.example", false)!;

            var rootSki = root.Extensions.OfType<X509SubjectKeyIdentifierExtension>().SingleOrDefault();
            Assert.IsNotNull(rootSki, $"{engine}/{algorithm}: root must carry an SKI");
            Assert.IsNotNull(leaf.Extensions[AkiOid], $"{engine}/{algorithm}: leaf must carry an AKI");
            StringAssert.Contains(
                Convert.ToHexString(leaf.Extensions[AkiOid]!.RawData),
                rootSki!.SubjectKeyIdentifier,
                $"{engine}/{algorithm}: leaf AKI must reference the root SKI", StringComparison.OrdinalIgnoreCase);

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(root.RawData));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            Assert.IsTrue(chain.Build(leaf), $"{engine}/{algorithm}: " +
                string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
            Assert.AreEqual(2, chain.ChainElements.Count);
        }
    }

    /// <summary>
    ///     A strict non-.NET verifier (OpenSSL) must accept the leaf: it follows the AKI to the root's SKI, so a
    ///     mismatched or malformed key identifier would fail here even though X509Chain tolerates it.
    /// </summary>
    [TestMethod]
    public void NewRootAndLeaf_VerifyWithOpenSsl_WhenAvailable()
    {
        var openssl = FindOpenSsl();
        if (openssl == null)
            Assert.Inconclusive("openssl not found on PATH.");

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ti-ossl-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            foreach (var algorithm in new[] { CertificateKeyAlgorithm.Rsa2048, CertificateKeyAlgorithm.EcdsaP256 })
            {
                using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                {
                    CertificateEngine = CertificateEngine.BouncyCastleFast,
                    LeafCertificateKeyAlgorithm = algorithm,
                };
                Assert.IsTrue(mgr.CreateRootCertificate(false));
                using var leaf = mgr.CreateCertificate("openssl-test.example", false)!;

                var rootPem = System.IO.Path.Combine(dir, algorithm + "-root.pem");
                var leafPem = System.IO.Path.Combine(dir, algorithm + "-leaf.pem");
                System.IO.File.WriteAllText(rootPem, mgr.RootCertificate!.ExportCertificatePem());
                System.IO.File.WriteAllText(leafPem, leaf.ExportCertificatePem());

                var psi = new System.Diagnostics.ProcessStartInfo(openssl, $"verify -CAfile \"{rootPem}\" \"{leafPem}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                Assert.IsTrue(process.WaitForExit(30000), "openssl verify timed out");
                Assert.AreEqual(0, process.ExitCode, $"{algorithm}: {output}");
                StringAssert.Contains(output, "OK");
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    private static string? FindOpenSsl()
    {
        var name = OperatingSystem.IsWindows() ? "openssl.exe" : "openssl";
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(path, name);
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    [TestMethod]
    public void LeafUnderLegacyRootWithoutSki_HasNoAki_AndStillValidates()
    {
        using var legacyKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Titanium Legacy Test Root", legacyKey, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var legacyRoot = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        Assert.IsNull(legacyRoot.Extensions[SkiOid], "test precondition: legacy root has no SKI");

        // Round-trip through PFX so the private key is usable by the BouncyCastle issuer plumbing.
        using var pfxRoot = X509CertificateLoader.LoadPkcs12(legacyRoot.Export(X509ContentType.Pfx, "x"), "x",
            X509KeyStorageFlags.Exportable);

        foreach (var engine in new[] { CertificateEngine.BouncyCastle, CertificateEngine.BouncyCastleFast })
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = engine,
            };
            mgr.RootCertificate = pfxRoot;
            using var leaf = mgr.CreateCertificate("legacy-ski.example", false)!;

            Assert.IsNull(leaf.Extensions[AkiOid], $"{engine}: no AKI when the issuer has no SKI");

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(pfxRoot.RawData));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.IsTrue(chain.Build(leaf), $"{engine}: " +
                string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        }
    }
}
