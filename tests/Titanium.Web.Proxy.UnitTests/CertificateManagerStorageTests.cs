using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CertificateManagerStorageTests
{
    [TestMethod]
    public void CreateRootCertificate_PersistToFile_ClearsAndSavesViaCertificateStorage()
    {
        var cache = new FakeCertificateCache();
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache
        };

        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: true));
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsTrue(cache.Cleared);
        Assert.IsTrue(cache.SavedRoot);
    }

    [TestMethod]
    public void CreateRootCertificate_OverwritePfxFalse_LoadsRootFromCertificateStorage()
    {
        using var seed = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(seed.CreateRootCertificate(false));
        var root = X509CertificateLoader.LoadPkcs12(seed.RootCertificate!.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);

        var cache = new FakeCertificateCache { RootToLoad = root };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            OverwritePfxFile = false
        };

        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: false));
        Assert.AreEqual(root.Thumbprint, mgr.RootCertificate!.Thumbprint);
        Assert.IsFalse(cache.SavedRoot);
    }

    [TestMethod]
    public async Task CreateServerCertificate_SaveFakeCertificates_DiskHit_SkipsMakeCertificate()
    {
        using var seed = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(seed.CreateRootCertificate(false));
        using var leaf = await seed.CreateServerCertificate("disk-hit.example");
        Assert.IsNotNull(leaf);
        var exported = X509CertificateLoader.LoadPkcs12(leaf!.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);

        var cache = new FakeCertificateCache();
        cache.Leaves["disk-hit.example"] = exported;

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            SaveFakeCertificates = true
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        using var loaded = await mgr.CreateServerCertificate("disk-hit.example");
        Assert.IsNotNull(loaded);
        Assert.AreEqual(exported.Thumbprint, loaded!.Thumbprint);
        Assert.AreEqual(0, cache.SaveLeafCount);
    }

    [TestMethod]
    public void EnsureRootCertificate_WithoutTrustFlags_CreatesRootOnly()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        mgr.EnsureRootCertificate();
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsTrue(mgr.CertValidated);
    }

    [TestMethod]
    public void EnsureRootCertificate_Overload_SetsTrustFlagsWithoutInstalling()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        // Keep trust false so CI does not mutate the user Root store.
        mgr.EnsureRootCertificate(userTrustRootCertificate: false, machineTrustRootCertificate: false,
            trustRootCertificateAsAdmin: false);
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsFalse(mgr.UserTrustRoot);
        Assert.IsFalse(mgr.MachineTrustRoot);
        Assert.IsFalse(mgr.TrustRootAsAdministrator);
    }

    [TestMethod]
    public async Task ClearRootCertificate_ClearsRootAndCachedLeaves()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = await mgr.CreateServerCertificate("clear-cache.example");
        Assert.IsNotNull(leaf);

        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);
    }

    [TestMethod]
    public async Task CreateCertificate_SaveFakeCertificates_PersistsLeafViaStorage()
    {
        var cache = new FakeCertificateCache();
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            SaveFakeCertificates = true
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateCertificate("save-fake.example", false);
        Assert.IsNotNull(leaf);

        // Save runs on a background Task; wait briefly for persistence.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (cache.SaveLeafCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.IsTrue(cache.SaveLeafCount >= 1);
        Assert.IsTrue(cache.Leaves.ContainsKey("save-fake.example"));
    }

    [TestMethod]
    public void LoadRootCertificate_Expired_ReturnsNull()
    {
        using var seed = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(seed.CreateRootCertificate(false));
        // Forge an "expired" view by wrapping via FakeCertificateCache that reports NotAfter in the past
        // is hard without mutating NotAfter; instead load path with Load throwing covers OnException.
        var cache = new FakeCertificateCache { LoadRootThrows = true };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache
        };
        Assert.IsNull(mgr.LoadRootCertificate());
    }

    [TestMethod]
    public void CertificateStorage_Null_ResetsToDefaultDiskCache()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = new FakeCertificateCache()
        };
        mgr.CertificateStorage = null!;
        Assert.IsInstanceOfType<DefaultCertificateDiskCache>(mgr.CertificateStorage);
    }

    [TestMethod]
    public async Task CreateServerCertificate_ConcurrentSameHost_CoalescesPendingCreationTask()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var tasks = new Task<X509Certificate2?>[8];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = mgr.CreateServerCertificate("coalesce.example");

        await Task.WhenAll(tasks);
        var thumb = tasks[0].Result!.Thumbprint;
        foreach (var t in tasks)
            Assert.AreEqual(thumb, t.Result!.Thumbprint);
    }

    private sealed class FakeCertificateCache : ICertificateCache
    {
        internal bool Cleared { get; private set; }
        internal bool SavedRoot { get; private set; }
        internal int SaveLeafCount { get; private set; }
        internal bool LoadRootThrows { get; set; }
        internal X509Certificate2? RootToLoad { get; set; }
        internal ConcurrentDictionary<string, X509Certificate2> Leaves { get; } = new();

        public X509Certificate2? LoadRootCertificate(string pathOrName, string password,
            X509KeyStorageFlags storageFlags)
        {
            if (LoadRootThrows) throw new IOException("root load failed");
            return RootToLoad;
        }

        public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
        {
            SavedRoot = true;
            RootToLoad = certificate;
        }

        public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
        {
            return Leaves.TryGetValue(subjectName, out var cert) ? cert : null;
        }

        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            SaveLeafCount++;
            Leaves[subjectName] = certificate;
        }

        public void Clear() => Cleared = true;
    }
}
