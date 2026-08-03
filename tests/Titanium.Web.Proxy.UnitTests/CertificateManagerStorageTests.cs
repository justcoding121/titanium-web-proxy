using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
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
        var cache = new FakeCertificateCache { RootToLoad = CreateExpiredCertificate("expired-root") };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache
        };
        Assert.IsNull(mgr.LoadRootCertificate());
    }

    [TestMethod]
    public void CreateRootCertificate_OverwriteFalse_ExpiredRootOnDisk_ReturnsFalse()
    {
        var cache = new FakeCertificateCache { RootToLoad = CreateExpiredCertificate("expired-on-disk") };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            OverwritePfxFile = false
        };
        Assert.IsFalse(mgr.CreateRootCertificate(persistToFile: false));
        Assert.IsNull(mgr.RootCertificate);
    }

    [TestMethod]
    public void CreateRootCertificate_LoadFailure_FallsBackToGeneratingRoot()
    {
        var cache = new FakeCertificateCache { LoadRootThrows = true };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            OverwritePfxFile = false
        };
        // Load throws are logged; CreateRootCertificate still mints a fresh root.
        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: false));
        Assert.IsNotNull(mgr.RootCertificate);
    }

    [TestMethod]
    public void CreateRootCertificate_ClearThrows_StillPersistsRoot()
    {
        var cache = new FakeCertificateCache { ClearThrows = true };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache
        };
        Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: true));
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.IsTrue(cache.SavedRoot);
    }

    [TestMethod]
    public async Task CreateServerCertificate_ExpiredDiskLeaf_RegeneratesFreshCertificate()
    {
        var expired = CreateExpiredCertificate("expired-leaf.example");
        var cache = new FakeCertificateCache();
        cache.Leaves["expired-leaf.example"] = expired;

        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            SaveFakeCertificates = true
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        var expiredThumbprint = expired.Thumbprint;
        using var loaded = await mgr.CreateServerCertificate("expired-leaf.example");
        Assert.IsNotNull(loaded);
        Assert.AreNotEqual(expiredThumbprint, loaded!.Thumbprint);
        Assert.IsTrue(loaded.NotAfter > DateTime.Now);
    }

    [TestMethod]
    public async Task CreateServerCertificate_DiskLoadFailure_FallsBackToGeneration()
    {
        var cache = new FakeCertificateCache { LoadLeafThrows = true };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            SaveFakeCertificates = true
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        using var loaded = await mgr.CreateServerCertificate("disk-fail.example");
        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.HasPrivateKey);
    }

    [TestMethod]
    public void CreateCertificate_ExpiredDiskLeaf_RegeneratesViaCreateCertificate()
    {
        var cache = new FakeCertificateCache();
        cache.Leaves["sync-expired.example"] = CreateExpiredCertificate("sync-expired.example");
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache,
            SaveFakeCertificates = true
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        using var leaf = mgr.CreateCertificate("sync-expired.example", false);
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.NotAfter > DateTime.Now);
    }

    [TestMethod]
    public void ClearRootCertificate_WhenStorageClearThrows_PropagatesException()
    {
        var cache = new FakeCertificateCache { ClearThrows = true };
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            CertificateStorage = cache
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        Assert.ThrowsExactly<IOException>(() => mgr.ClearRootCertificate());
    }

    [TestMethod]
    public void LoadRootCertificate_IOException_ReturnsNull()
    {
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
        internal bool LoadLeafThrows { get; set; }
        internal bool ClearThrows { get; set; }
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
            if (LoadLeafThrows) throw new IOException("leaf load failed");
            return Leaves.TryGetValue(subjectName, out var cert) ? cert : null;
        }

        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            SaveLeafCount++;
            Leaves[subjectName] = certificate;
        }

        public void Clear()
        {
            if (ClearThrows) throw new IOException("clear failed");
            Cleared = true;
        }
    }

    private static X509Certificate2 CreateExpiredCertificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));
    }
}
