using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
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

}
