using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class AbsoluteRootLeafCacheTests
{
    [TestMethod]
    public void AbsoluteRoot_SaveAndLoadLeaf_UsesCrtsBesideRoot()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 Exportable disk-cache characterization is Windows-focused.");

        var rootDir = Path.Combine(Path.GetTempPath(), "twp-abs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        var rootPfx = Path.Combine(rootDir, "rootCert.pfx");
        try
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            Assert.IsNotNull(mgr.RootCertificate);

            var cache = new DefaultCertificateDiskCache();
            cache.SaveRootCertificate(rootPfx, string.Empty, mgr.RootCertificate);

            using var leaf = mgr.CreateCertificate("leaf.example.com", false)!;
            cache.SaveCertificate("leaf.example.com", leaf);

            var leafPath = Path.Combine(rootDir, "crts", "leaf.example.com.pfx");
            Assert.IsTrue(File.Exists(leafPath), "leaf must live under absolute-root/crts/");

            var loaded = cache.LoadCertificate("leaf.example.com", X509KeyStorageFlags.Exportable);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(leaf.Thumbprint, loaded!.Thumbprint);
            loaded.Dispose();

            var sharedLeaf = Path.Combine(
                DefaultCertificateDiskCache.GetSharedLeafCertificateDirectory(),
                "leaf.example.com.pfx");
            Assert.IsFalse(File.Exists(sharedLeaf), "must not write into shared Titanium.Web.Proxy/crts");
        }
        finally
        {
            try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void AbsoluteRoot_Clear_DoesNotTouchSharedCrts()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 Exportable disk-cache characterization is Windows-focused.");

        var rootDir = Path.Combine(Path.GetTempPath(), "twp-abs2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        var rootPfx = Path.Combine(rootDir, "rootCert.pfx");
        var shared = DefaultCertificateDiskCache.GetSharedLeafCertificateDirectory();
        Directory.CreateDirectory(shared);
        var marker = Path.Combine(shared, "keep-me-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(marker, [1, 2, 3]);
        try
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            var cache = new DefaultCertificateDiskCache();
            cache.SaveRootCertificate(rootPfx, string.Empty, mgr.RootCertificate!);
            using var leaf = mgr.CreateCertificate("a.example", false)!;
            cache.SaveCertificate("a.example", leaf);
            Assert.IsTrue(Directory.Exists(Path.Combine(rootDir, "crts")));

            cache.Clear();
            Assert.IsFalse(Directory.Exists(Path.Combine(rootDir, "crts")));
            Assert.IsTrue(File.Exists(marker), "shared crts marker must survive Clear of absolute-root cache");
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best-effort */ }
            try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void TwoAbsoluteRoots_DoNotShareLeaves()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 Exportable disk-cache characterization is Windows-focused.");

        var dirA = Path.Combine(Path.GetTempPath(), "twp-a-" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "twp-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            var cacheA = new DefaultCertificateDiskCache();
            cacheA.SaveRootCertificate(Path.Combine(dirA, "rootCert.pfx"), string.Empty, mgr.RootCertificate!);
            using var leaf = mgr.CreateCertificate("shared-name.example", false)!;
            cacheA.SaveCertificate("shared-name.example", leaf);

            var cacheB = new DefaultCertificateDiskCache();
            cacheB.SaveRootCertificate(Path.Combine(dirB, "rootCert.pfx"), string.Empty, mgr.RootCertificate!);

            Assert.IsTrue(File.Exists(Path.Combine(dirA, "crts", "shared-name.example.pfx")));
            Assert.IsFalse(File.Exists(Path.Combine(dirB, "crts", "shared-name.example.pfx")));
            Assert.IsNull(cacheB.LoadCertificate("shared-name.example", X509KeyStorageFlags.Exportable));
        }
        finally
        {
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch { /* best-effort */ }
            try { if (Directory.Exists(dirB)) Directory.Delete(dirB, true); } catch { /* best-effort */ }
        }
    }
}

[TestClass]
public class SameCommonNameStoreCandidateTests
{
    [TestMethod]
    public void Filter_SameCnDifferentThumbprint_IsOrphan()
    {
        using var current = CreateSelfSigned("CN=Titanium Root Certificate Authority");
        using var orphan = CreateSelfSigned("CN=Titanium Root Certificate Authority");
        Assert.AreNotEqual(current.Thumbprint, orphan.Thumbprint);
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(
            orphan, "Titanium Root Certificate Authority", current.Thumbprint));
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(
            current, "Titanium Root Certificate Authority", current.Thumbprint));
    }

    [TestMethod]
    public void Filter_DifferentCn_NotSelected()
    {
        using var other = CreateSelfSigned("CN=Other Root");
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(
            other, "Titanium Root Certificate Authority", keepThumbprint: null));
    }

    [TestMethod]
    public void Filter_NullKeepThumbprint_SelectsMatchingCn()
    {
        using var cert = CreateSelfSigned("CN=Titanium Root Certificate Authority");
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(
            cert, "Titanium Root Certificate Authority", keepThumbprint: null));
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [TestMethod]
    public void EvictCertificate_InvalidatesSslContextEntry()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateCertificate("evict.example.com", false)!;

        var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cached = cacheField.GetValue(mgr)!;
        var cachedType = cached.GetType();
        if (!(bool)cachedType.GetMethod("ContainsKey")!.Invoke(cached, ["evict.example.com"])!)
        {
            var cachedCertType = cachedType.GetGenericArguments()[1];
            var cachedCert = Activator.CreateInstance(cachedCertType, leaf)!;
            Assert.IsTrue((bool)cachedType.GetMethod("TryAdd")!.Invoke(cached, ["evict.example.com", cachedCert])!);
        }

        var invalidate = typeof(CertificateManager).GetMethod("InvalidateSslCertificateContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        invalidate.Invoke(mgr, [leaf]);

        var evict = typeof(CertificateManager).GetMethod("EvictCertificate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        evict.Invoke(mgr, ["evict.example.com"]);

        Assert.IsFalse((bool)cachedType.GetMethod("ContainsKey")!.Invoke(cached, ["evict.example.com"])!);
        evict.Invoke(mgr, ["missing.example.com"]);
    }

    [TestMethod]
    public void IsSameCommonNameStoreCandidate_KeepThumbprintBranches()
    {
        using var leaf = CreateSelfSigned("CN=cn-check.example.com");
        var cn = leaf.GetNameInfo(X509NameType.SimpleName, false)!;
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(leaf, "Other CN", null));
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(leaf, cn, null));
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(leaf, cn, leaf.Thumbprint));
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(leaf, cn, "DEADBEEF"));
    }
    [TestMethod]
    public void InvalidateSslCertificateContext_Twice_AndDisposePendingEvictions()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateCertificate("invalidate-twice.example.com", false)!;
        var invalidate = typeof(CertificateManager).GetMethod("InvalidateSslCertificateContext",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        invalidate.Invoke(mgr, [leaf]);
        invalidate.Invoke(mgr, [leaf]);

        var pendingField = typeof(CertificateManager).GetField("pendingDisposals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = pendingField.GetValue(mgr)!;
        var itemType = pending.GetType().GetGenericArguments()[0];
        var disposable = X509CertificateLoader.LoadCertificate(leaf.RawData);
        var item = Activator.CreateInstance(itemType, disposable, DateTime.UtcNow.AddMinutes(-5))!;
        pending.GetType().GetMethod("Enqueue")!.Invoke(pending, [item]);
        typeof(CertificateManager).GetMethod("DisposePendingEvictions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(mgr, null);
        Assert.AreEqual(0, (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!);
    }
}