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
            chain.ChainPolicy.CustomTrustStore.Add(new X509Certificate2(root.RawData));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            Assert.IsTrue(chain.Build(leaf), $"{engine}/{algorithm}: " +
                string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
            Assert.AreEqual(2, chain.ChainElements.Count);
        }
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
        using var pfxRoot = new X509Certificate2(legacyRoot.Export(X509ContentType.Pfx, "x"), "x",
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
            chain.ChainPolicy.CustomTrustStore.Add(new X509Certificate2(pfxRoot.RawData));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.IsTrue(chain.Build(leaf), $"{engine}: " +
                string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        }
    }
}
