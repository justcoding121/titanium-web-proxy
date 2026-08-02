using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Characterization for issue #889: DefaultCertificateDiskCache PKCS#12 save/load on Windows.
/// </summary>
[TestClass]
public class DefaultCertificateDiskCacheTests
{
    [TestMethod]
    public void DefaultCertificateDiskCache_SaveAndLoadRoot_RoundTripsPrivateKey()
    {
        // #889: Export(Pkcs12) + LoadPkcs12 must work on supported platforms (this CI is Windows).
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 Exportable disk-cache characterization is Windows-focused.");

        var pfxPath = Path.Combine(Path.GetTempPath(), $"twp-disk-{Guid.NewGuid():N}.pfx");
        const string password = "disk-cache-pass";

        try
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            Assert.IsNotNull(mgr.RootCertificate);

            var cache = new DefaultCertificateDiskCache();
            cache.SaveRootCertificate(pfxPath, password, mgr.RootCertificate);

            var loaded = cache.LoadRootCertificate(pfxPath, password, X509KeyStorageFlags.Exportable);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(mgr.RootCertificate.Thumbprint, loaded.Thumbprint);
            Assert.IsTrue(loaded.HasPrivateKey, "Reloaded root must retain its private key");
            loaded.Dispose();
        }
        finally
        {
            try
            {
                if (File.Exists(pfxPath)) File.Delete(pfxPath);
            }
            catch
            {
                // best-effort
            }
        }
    }

    /// <summary>
    ///     Phase E.14 ("Bound in-memory and disk certificate caches"): unlike the in-memory cache, the
    ///     on-disk leaf-certificate cache has no other eviction path at all, so <c>PruneToMaxEntries</c>
    ///     is the only thing preventing one permanent .pfx file per ever-visited hostname from
    ///     accumulating forever.
    /// </summary>
    [TestMethod]
    public void PruneToMaxEntries_DeletesOldestFilesFirst_KeepingOnlyTheBound()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 Exportable disk-cache characterization is Windows-focused.");

        var cache = new DefaultCertificateDiskCache();
        var certPathField = typeof(DefaultCertificateDiskCache).GetMethod("GetCertificatePath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(certPathField);
        var certDir = (string)certPathField.Invoke(cache, new object[] { true })!;

        try
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            var names = new[] { "prune-a.example", "prune-b.example", "prune-c.example" };
            foreach (var name in names)
            {
                using var cert = mgr.CreateCertificate(name, false);
                cache.SaveCertificate(name, cert);
                // Ensure distinct LastWriteTimeUtc ordering between files created back-to-back.
                Thread.Sleep(20);
            }

            cache.PruneToMaxEntries(2);

            var remaining = Directory.GetFiles(certDir, "*.pfx")
                .Select(Path.GetFileNameWithoutExtension).ToArray();
            Assert.AreEqual(2, remaining.Length, "cache directory should be pruned down to the bound");
            CollectionAssert.DoesNotContain(remaining, "prune-a.example",
                "the oldest file should have been pruned first");
        }
        finally
        {
            foreach (var name in new[] { "prune-a.example", "prune-b.example", "prune-c.example" })
                try { File.Delete(Path.Combine(certDir, name + ".pfx")); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void LoadRootCertificate_MissingFile_ReturnsNull()
    {
        var cache = new DefaultCertificateDiskCache();
        var missing = Path.Combine(Path.GetTempPath(), $"twp-missing-{Guid.NewGuid():N}.pfx");
        var loaded = cache.LoadRootCertificate(missing, "unused", X509KeyStorageFlags.Exportable);
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public void LoadCertificate_CorruptPfx_ReturnsNull()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 disk-cache characterization is Windows-focused.");

        var cache = new DefaultCertificateDiskCache();
        var certPathField = typeof(DefaultCertificateDiskCache).GetMethod("GetCertificatePath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(certPathField);
        var certDir = (string)certPathField.Invoke(cache, new object[] { true })!;
        var subject = $"corrupt-{Guid.NewGuid():N}.example";
        var filePath = Path.Combine(certDir, subject + ".pfx");

        try
        {
            File.WriteAllBytes(filePath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF });
            var loaded = cache.LoadCertificate(subject, X509KeyStorageFlags.Exportable);
            Assert.IsNull(loaded, "Corrupt PKCS#12 must be treated as a cache miss.");
        }
        finally
        {
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { /* best-effort */ }
        }
    }

}
