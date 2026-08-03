using System;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
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
        var root = new X509Certificate2(seed.RootCertificate!.Export(X509ContentType.Pfx), (string?)null,
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
        var exported = new X509Certificate2(leaf!.Export(X509ContentType.Pfx), (string?)null,
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
        internal X509Certificate2? RootToLoad { get; set; }
        internal ConcurrentDictionary<string, X509Certificate2> Leaves { get; } = new();

        public X509Certificate2? LoadRootCertificate(string pathOrName, string password,
            X509KeyStorageFlags storageFlags) => RootToLoad;

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
