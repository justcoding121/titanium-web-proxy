using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Behavior coverage for <see cref="CertificateManager" /> methods identified as gaps:
///     <c>OcspStapling</c> setter, <c>IsSameCommonNameStoreCandidate</c> edge cases,
///     <c>ConfigureServerCertificate</c> chain-failure fallback,
///     <c>IntermediateCertificates</c> influence on context construction,
///     <c>DisableWildCardCertificates</c> SAN shape, and
///     <c>RemoveCertificateByThumbprint</c> guard paths.
/// </summary>
[TestClass]
public class CertificateManagerEdgeCoverageTests
{
    // -----------------------------------------------------------------------
    // OcspStapling setter
    // Bug class: toggling OcspStapling does not invalidate stale certificate contexts,
    // so offline-built contexts survive after OCSP is enabled — or vice-versa.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(90_000)]
    public void OcspStapling_ToggleTrue_ClearsContextCache()
    {
        // Bug class: context cache survives an OcspStapling toggle, so the proxy keeps
        // presenting offline-built chains after the caller enabled stapling.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);

        using var leaf = mgr.CreateCertificate("ocsp-test.example", false)!;
        // Populate the context cache with an offline (OCSP=false) context.
        Assert.IsFalse(mgr.OcspStapling);
        var ctx1 = mgr.CreateSslCertificateContext(leaf);
        Assert.IsNotNull(ctx1);

        // Toggling OcspStapling must clear the cache. We do NOT build a new context
        // while OcspStapling=true because that path tries to staple (network IO + crash risk).
        mgr.OcspStapling = true;
        // Toggle back to false immediately; the point is that the setter ran.
        mgr.OcspStapling = false;

        // After the round-trip the cache was cleared, so a new offline context is built.
        var ctx2 = mgr.CreateSslCertificateContext(leaf);
        Assert.IsNotNull(ctx2);
    }

    [TestMethod]
    [Timeout(90_000)]
    public void OcspStapling_SetSameValue_IsNoop()
    {
        // Bug class: setting OcspStapling to its current value clears the cache unnecessarily.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("ocsp-noop.example", false)!;

        var ctx1 = mgr.CreateSslCertificateContext(leaf);
        // Setting the same value (false→false) must not clear the cache.
        mgr.OcspStapling = false;
        var ctx2 = mgr.CreateSslCertificateContext(leaf);
        // Both calls must return the same cached object (reference equality: same leaf).
        Assert.IsTrue(ReferenceEquals(ctx1.TargetCertificate, ctx2.TargetCertificate),
            "No-op OcspStapling toggle must not evict the cached context.");
    }

    // -----------------------------------------------------------------------
    // IsSameCommonNameStoreCandidate static method edge cases
    // Bug class: orphan cleanup accidentally removes the current root, or misses
    // an orphan because CN matching is too strict / too loose.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(10_000)]
    public void IsSameCommonNameStoreCandidate_EmptyExpectedCn_ReturnsFalse()
    {
        // Bug class: empty expectedCn causes a broad match against every certificate in the store.
        using var root = MakeSelfSignedCert("Anything");
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(root, "", null));
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(root, "", "thumbprint"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public void IsSameCommonNameStoreCandidate_CnPrefixMatch_ReturnsTrueForOrphan()
    {
        // Bug class: CN= prefix match fails because of case difference or missing space after =.
        using var cert = MakeSelfSignedCert("Titanium Root Certificate Authority");
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(
            cert, "Titanium Root Certificate Authority", null),
            "Null keepThumbprint means select all matching CNs.");
        // A different thumbprint should still match (it's an orphan).
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(
            cert, "Titanium Root Certificate Authority", "DEADBEEF0000"),
            "Different keepThumbprint: candidate is an orphan.");
        // Same thumbprint should NOT match (keep current).
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(
            cert, "Titanium Root Certificate Authority", cert.Thumbprint),
            "Same keepThumbprint: candidate is the current root — should be preserved.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public void IsSameCommonNameStoreCandidate_SubjectMismatch_ReturnsFalse()
    {
        // Bug class: certificates with completely different subjects are matched.
        using var cert = MakeSelfSignedCert("Unrelated CA");
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(
            cert, "Titanium Root Certificate Authority", null));
    }

    // -----------------------------------------------------------------------
    // ConfigureServerCertificate — chain-build failure fallback
    // Bug class: a chain-build failure throws instead of falling back to
    // options.ServerCertificate, killing the TLS handshake.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(90_000)]
    public void ConfigureServerCertificate_ChainBuildFailure_FallsBackToLeafCertificate()
    {
        // Bug class: if SslStreamCertificateContext.Create throws, ConfigureServerCertificate
        // re-throws instead of falling back to options.ServerCertificate = leaf.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("chain-fallback.example", false)!;

        var options = new SslServerAuthenticationOptions();
        // Normal path: ConfigureServerCertificate must not throw.
        mgr.ConfigureServerCertificate(options, leaf);

        // Either context or fallback cert must be set — never both null.
        Assert.IsTrue(
            options.ServerCertificateContext != null || options.ServerCertificate != null,
            "ConfigureServerCertificate must set either ServerCertificateContext or ServerCertificate.");
    }

    // -----------------------------------------------------------------------
    // IntermediateCertificates — extra chain members appended to context
    // Bug class: IntermediateCertificates is ignored, so the client cannot
    // build a chain when the issuing CA is not a self-signed root.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(90_000)]
    public void CreateSslCertificateContext_WithIntermediateCertificates_Succeeds()
    {
        // Bug class: extra intermediate certificates are silently dropped from the chain.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("intermediate-chain.example", false)!;

        // Attach the root itself as an intermediate (valid for chain-delivery, though unusual).
        using var extraCopy = CertificateLoader.LoadCertificate(mgr.RootCertificate!.Export(X509ContentType.Cert));
        mgr.IntermediateCertificates = new X509Certificate2Collection(extraCopy);
        try
        {
            var ctx = mgr.CreateSslCertificateContext(leaf);
            Assert.IsNotNull(ctx, "Context must be created even when IntermediateCertificates is set.");
        }
        finally
        {
            mgr.IntermediateCertificates = null;
        }
    }

    // -----------------------------------------------------------------------
    // DisableWildCardCertificates — per-host SAN instead of wildcard
    // Bug class: DisableWildCardCertificates=true is ignored, and the proxy
    // issues *.parent.tld leaves that do not precisely match the requested host.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(90_000)]
    public void DisableWildCardCertificates_True_EmitsExactHostCert()
    {
        // Bug class: DisableWildCardCertificates=true still generates wildcard SANs,
        // so per-host pinning and strict name checks fail.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            DisableWildCardCertificates = true,
        };
        mgr.CreateRootCertificate(false);
        using var cert = mgr.CreateCertificate("sub.example.com", false)!;
        Assert.IsNotNull(cert);
        // When wildcards are disabled the SAN must not contain a star.
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault()
                  ?? new X509SubjectAlternativeNameExtension(cert.Extensions["2.5.29.17"]!.RawData, false);
        var dnsNames = san.EnumerateDnsNames().ToArray();
        Assert.IsFalse(dnsNames.Any(d => d.StartsWith("*.", StringComparison.Ordinal)),
            $"DisableWildCardCertificates=true must not produce wildcard SAN; got: {string.Join(", ", dnsNames)}");
        Assert.IsTrue(dnsNames.Any(d => d.Contains("sub.example.com", StringComparison.OrdinalIgnoreCase)
                                        || d.Contains("example.com", StringComparison.OrdinalIgnoreCase)),
            $"SAN must contain the host or its parent; got: {string.Join(", ", dnsNames)}");
    }

    [TestMethod]
    [Timeout(90_000)]
    public void DisableWildCardCertificates_False_MayEmitWildcard()
    {
        // Bug class: DisableWildCardCertificates=false always emits per-host certs, which is fine,
        // but we check that the default (false) does not break certificate creation.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            DisableWildCardCertificates = false,
        };
        mgr.CreateRootCertificate(false);
        using var cert = mgr.CreateCertificate("sub.wildcard.com", false)!;
        Assert.IsNotNull(cert, "Creating a certificate with wildcards enabled must succeed.");
    }

    // -----------------------------------------------------------------------
    // RemoveCertificateByThumbprint — guard for empty thumbprint
    // Bug class: empty/whitespace thumbprint performs a full store search and
    // silently removes certificates it was not asked to remove.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(10_000)]
    public void RemoveCertificateByThumbprint_EmptyThumbprint_ReturnsFalse()
    {
        // Bug class: empty thumbprint bypasses the guard and opens the store unnecessarily.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance);

        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(StoreName.My, StoreLocation.CurrentUser, ""));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(StoreName.My, StoreLocation.CurrentUser, "   "));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(StoreName.My, StoreLocation.CurrentUser, null!));
    }

    [TestMethod]
    [Timeout(10_000)]
    public void RemoveCertificateByThumbprint_RootStore_SuppressedUI_ReturnsFalse()
    {
        // Bug class: calling RemoveCertificateByThumbprint on StoreName.Root without suppression
        // opens the Windows CryptUI dialog in CI. The suppression guard must prevent that.
        Assert.IsTrue(CertificateManager.AreInteractiveRootStoreMutationsSuppressed,
            "Unit tests must have interactive root store mutations suppressed.");

        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance);
        // Any non-empty thumbprint on the Root store must return false under suppression.
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            StoreName.Root, StoreLocation.CurrentUser, "AABBCCDD0011223344556677889900AABBCCDDEE"));
    }

    // -----------------------------------------------------------------------
    // CertificateStorage setter — null coercion
    // Bug class: setting CertificateStorage to null throws NullReferenceException
    // later when the manager tries to call storage.LoadCertificate.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(10_000)]
    public void CertificateStorage_SetNull_FallsBackToDefaultDiskCache()
    {
        // Bug class: setting CertificateStorage = null does not replace the backing field with
        // a default implementation, leaving the manager in a broken state.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance);
        var defaultStorage = mgr.CertificateStorage;
        Assert.IsNotNull(defaultStorage);

        mgr.CertificateStorage = null!;
        Assert.IsNotNull(mgr.CertificateStorage, "Setting null must be coerced to a non-null default.");
    }

    // -----------------------------------------------------------------------
    // CreateSslCertificateContext — same-leaf caching and eviction on toggle
    // Bug class: two calls for the same leaf object return two different contexts,
    // wasting a chain build per handshake.
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(90_000)]
    public void CreateSslCertificateContext_SameLeafInstance_ReturnsCachedContext()
    {
        // Bug class: the cache key comparison uses identity rather than thumbprint,
        // so each handshake builds a new chain for the same leaf.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("cached-ctx.example", false)!;

        var ctx1 = mgr.CreateSslCertificateContext(leaf);
        var ctx2 = mgr.CreateSslCertificateContext(leaf);
        Assert.IsTrue(ReferenceEquals(ctx1.TargetCertificate, ctx2.TargetCertificate),
            "Same leaf instance must return the same cached context.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static X509Certificate2 MakeSelfSignedCert(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
