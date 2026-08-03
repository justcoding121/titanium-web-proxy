using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Logging;
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
    public async Task PruneToMaxEntries_DeletesOldestFilesFirst_KeepingOnlyTheBound()
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
                cache.SaveCertificate(name, cert!);
                // Ensure distinct LastWriteTimeUtc ordering between files created back-to-back.
                await Task.Delay(20);
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
    public void CertificateLoader_LoadCertificate_AndLoadPkcs12_RoundTrip()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var cert = mgr.CreateCertificate("loader.example", false);

        var raw = cert!.Export(X509ContentType.Cert);
        using var loaded = Titanium.Web.Proxy.Network.Certificate.CertificateLoader.LoadCertificate(raw);
        Assert.AreEqual(cert.Thumbprint, loaded.Thumbprint);

        var pfx = cert.Export(X509ContentType.Pkcs12, "pw");
        using var loadedPfx = Titanium.Web.Proxy.Network.Certificate.CertificateLoader.LoadPkcs12(
            pfx, "pw", X509KeyStorageFlags.Exportable);
        Assert.AreEqual(cert.Thumbprint, loadedPfx.Thumbprint);
        Assert.IsTrue(loadedPfx.HasPrivateKey);
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

    [TestMethod]
    public void LoadCertificate_IOExceptionWhileReading_ReturnsNull()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("Exclusive file-lock characterization is Windows-focused.");

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var pfxPath = Path.Combine(Path.GetTempPath(), $"twp-io-{Guid.NewGuid():N}.pfx");
        var cache = new DefaultCertificateDiskCache();
        try
        {
            cache.SaveRootCertificate(pfxPath, string.Empty, mgr.RootCertificate!);
            using var lockStream = new FileStream(pfxPath, FileMode.Open, FileAccess.Read, FileShare.None);
            var loaded = cache.LoadRootCertificate(pfxPath, string.Empty, X509KeyStorageFlags.Exportable);
            Assert.IsNull(loaded, "IOException during read must be treated as a cache miss.");
        }
        finally
        {
            try { if (File.Exists(pfxPath)) File.Delete(pfxPath); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void WriteFileAtomic_ReplaceFailure_CleansUpTempAndRethrows()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("File.Replace lock characterization is Windows-focused.");

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var targetPath = Path.Combine(Path.GetTempPath(), $"twp-atomic-{Guid.NewGuid():N}.pfx");
        var originalBytes = mgr.RootCertificate!.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(targetPath, originalBytes);

        var writeFileAtomic = typeof(DefaultCertificateDiskCache).GetMethod("WriteFileAtomic",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(writeFileAtomic);

        var replacement = mgr.CreateCertificate("atomic-replace.example", false)!.Export(X509ContentType.Pkcs12);
        var dir = Path.GetDirectoryName(targetPath)!;
        var beforeTemps = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase)).ToArray();

        using (var lockStream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
                writeFileAtomic.Invoke(null, new object[] { targetPath, replacement }));
            Assert.IsInstanceOfType<IOException>(ex.InnerException);
        }

        CollectionAssert.AreEquivalent(beforeTemps,
            Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase)).ToArray(),
            "Failed atomic replace must not leave stray temp files behind.");
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(targetPath),
            "Failed atomic replace must leave the original file intact.");
    }

    [TestMethod]
    public void Clear_ReadOnlyDirectory_DoesNotThrow()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("Read-only directory characterization is Windows-focused.");

        var cache = new DefaultCertificateDiskCache();
        var certPathMethod = typeof(DefaultCertificateDiskCache).GetMethod("GetCertificatePath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(certPathMethod);
        var certDir = (string)certPathMethod.Invoke(cache, new object[] { true })!;

        try
        {
            Directory.CreateDirectory(certDir);
            File.WriteAllText(Path.Combine(certDir, "marker.txt"), "x");
            File.SetAttributes(certDir, FileAttributes.ReadOnly);

            cache.Clear();
        }
        finally
        {
            try
            {
                if (Directory.Exists(certDir))
                {
                    File.SetAttributes(certDir, FileAttributes.Normal);
                    Directory.Delete(certDir, true);
                }
            }
            catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void PruneToMaxEntries_LockedOldestFile_SkipsLockedEntryAndPrunesOthers()
    {
        if (!RunTime.IsWindows)
            Assert.Inconclusive("PKCS#12 disk-cache characterization is Windows-focused.");

        var cache = new DefaultCertificateDiskCache();
        var certPathMethod = typeof(DefaultCertificateDiskCache).GetMethod("GetCertificatePath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(certPathMethod);
        var certDir = (string)certPathMethod.Invoke(cache, new object[] { true })!;

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var names = new[] { "prune-lock-a.example", "prune-lock-b.example", "prune-lock-c.example" };
        FileStream? lockStream = null;
        try
        {
            foreach (var name in names)
            {
                using var cert = mgr.CreateCertificate(name, false);
                cache.SaveCertificate(name, cert!);
                Thread.Sleep(20);
            }

            var oldestPath = Path.Combine(certDir, names[0] + ".pfx");
            lockStream = new FileStream(oldestPath, FileMode.Open, FileAccess.Read, FileShare.None);
            cache.PruneToMaxEntries(1);

            var remaining = Directory.GetFiles(certDir, "*.pfx")
                .Select(Path.GetFileNameWithoutExtension).ToArray();
            Assert.AreEqual(2, remaining.Length, "one locked file plus one pruned survivor should remain");
            CollectionAssert.Contains(remaining, names[0],
                "the locked oldest file should survive until a later prune pass");
        }
        finally
        {
            lockStream?.Dispose();
            foreach (var name in names)
                try { File.Delete(Path.Combine(certDir, name + ".pfx")); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void PruneToMaxEntries_NonPositiveBound_IsNoOp()
    {
        var cache = new DefaultCertificateDiskCache();
        cache.PruneToMaxEntries(null);
        cache.PruneToMaxEntries(0);
        cache.PruneToMaxEntries(-1);
    }

    [TestMethod]
    public void WarnIfLegacyRootIsOrphaned_LegacyRootWithoutNewRoot_LogsOneTimeWarning()
    {
        var orphanFlag = typeof(DefaultCertificateDiskCache).GetField("orphanedLegacyRootNoticeLogged",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(orphanFlag);
        orphanFlag.SetValue(null, false);

        var warnMethod = typeof(DefaultCertificateDiskCache).GetMethod("WarnIfLegacyRootIsOrphaned",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(warnMethod);

        var legacyDir = Path.Combine(Path.GetTempPath(), $"twp-legacy-{Guid.NewGuid():N}");
        var newDir = Path.Combine(Path.GetTempPath(), $"twp-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(newDir);

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastleFast
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        var legacyRootPath = Path.Combine(legacyDir, "rootCert.pfx");
        File.WriteAllBytes(legacyRootPath, mgr.RootCertificate!.Export(X509ContentType.Pkcs12));

        var capturing = new CapturingLogger();
        var previousLogger = ProxyDiagnostics.Logger;
        try
        {
            ProxyDiagnostics.Logger = capturing;
            warnMethod.Invoke(null, new object[] { legacyDir, newDir });
            warnMethod.Invoke(null, new object[] { legacyDir, newDir });

            Assert.AreEqual(1, capturing.WarningCount, "orphan notice must be logged only once per process");
            Assert.IsTrue(capturing.Messages.Exists(m =>
                m.Contains("orphaned", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            ProxyDiagnostics.Logger = previousLogger;
            orphanFlag.SetValue(null, false);
            try { if (File.Exists(legacyRootPath)) File.Delete(legacyRootPath); } catch { /* best-effort */ }
            try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir); } catch { /* best-effort */ }
            try { if (Directory.Exists(newDir)) Directory.Delete(newDir); } catch { /* best-effort */ }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (logLevel == LogLevel.Warning) WarningCount++;
        }
    }
}
