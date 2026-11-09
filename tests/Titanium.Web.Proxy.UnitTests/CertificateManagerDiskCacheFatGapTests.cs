#pragma warning disable TWP001
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager disk-cache / Ssl context / ProxyServer listen edges still soft:
///     SaveFakeCertificates persist+reload via <see cref="ICertificateCache"/>, same-host burst,
///     ClearRoot after disk-backed leaf, and hot-add endpoint while running.
/// </summary>
[TestClass]
public class CertificateManagerDiskCacheFatGapTests
{
    private static CertificateManager NewMgr(ICertificateCache? storage = null)
    {
        var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        if (storage is not null)
            mgr.CertificateStorage = storage;
        return mgr;
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task SaveFakeCertificates_PersistsLeaf_AndSecondManagerReloads()
    {
        // Bug class: leaf is written to storage but a fresh manager never reloads it and
        // re-runs keygen on every process start.
        var cache = new RecordingCertificateCache();
        using (var mgr = NewMgr(cache))
        {
            mgr.SaveFakeCertificates = true;
            mgr.CreateRootCertificate(persistToFile: false);
            using var leaf = await mgr.CreateServerCertificate("disk-cache.example");
            Assert.IsNotNull(leaf);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (cache.SaveLeafCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsTrue(cache.SaveLeafCount >= 1, "SaveFakeCertificates must persist the leaf.");
            Assert.IsTrue(cache.Leaves.ContainsKey("disk-cache.example"));
        }

        using var mgr2 = NewMgr(cache);
        mgr2.SaveFakeCertificates = true;
        mgr2.CreateRootCertificate(persistToFile: false);
        using var loaded = await mgr2.CreateServerCertificate("disk-cache.example");
        Assert.IsNotNull(loaded);
        Assert.IsTrue(loaded!.HasPrivateKey);
        Assert.AreEqual(0, cache.SaveLeafCountAfterReload,
            "Disk hit must not re-save a freshly loaded leaf.");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task CreateServerCertificate_SameHostBurst_SharesUsableLeaf()
    {
        // Bug class: concurrent same-host mint races leave one task with a disposed cert.
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => mgr.CreateServerCertificate("burst-same.example"))
            .ToArray();
        var certs = await Task.WhenAll(tasks);
        try
        {
            Assert.IsTrue(certs.All(c => c is not null && c.HasPrivateKey));
            var thumbs = certs.Select(c => c!.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.AreEqual(1, thumbs.Length, "Same host must share one cached leaf under burst mint.");
        }
        finally
        {
            foreach (var c in certs.Distinct())
                c?.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClearRootCertificate_AfterDiskBackedLeaf_AllowsRemint()
    {
        var cache = new RecordingCertificateCache();
        using var mgr = NewMgr(cache);
        mgr.SaveFakeCertificates = true;
        mgr.CreateRootCertificate(persistToFile: false);
        using var first = await mgr.CreateServerCertificate("clear-disk.example");
        Assert.IsNotNull(first);

        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);

        mgr.CreateRootCertificate(persistToFile: false);
        using var second = await mgr.CreateServerCertificate("clear-disk.example");
        Assert.IsNotNull(second);
        Assert.IsTrue(second!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateSslCertificateContext_ReturnsCachedContextForSameLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(persistToFile: false);
        using var leaf = await mgr.CreateServerCertificate("ctx-disk.example");
        Assert.IsNotNull(leaf);
        var ctx1 = mgr.CreateSslCertificateContext(leaf!);
        var ctx2 = mgr.CreateSslCertificateContext(leaf);
        Assert.IsNotNull(ctx1);
        Assert.AreSame(ctx1, ctx2, "Same leaf must reuse SslStreamCertificateContext.");
        Assert.AreEqual(leaf.Thumbprint, ctx1.TargetCertificate.Thumbprint,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RecordingCertificateCache : ICertificateCache
    {
        internal int SaveLeafCount { get; private set; }
        internal int SaveLeafCountAfterReload { get; private set; }
        private bool reloading;
        internal ConcurrentDictionary<string, X509Certificate2> Leaves { get; } = new();

        public X509Certificate2? LoadRootCertificate(string pathOrName, string password,
            X509KeyStorageFlags storageFlags) => null;

        public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
        {
        }

        public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
        {
            if (!Leaves.TryGetValue(subjectName, out var cert))
                return null;
            reloading = true;
            // Return a fresh PKCS#12 copy so the caller owns disposal.
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.Exportable);
        }

        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            if (reloading)
            {
                SaveLeafCountAfterReload++;
                return;
            }

            SaveLeafCount++;
            Leaves[subjectName] = X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        public void Clear() => Leaves.Clear();
    }
}
#pragma warning restore TWP001
