using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class CertificateManagerTests
    {
        private static readonly string[] hostNames
            = { "facebook.com", "youtube.com", "google.com", "bing.com", "yahoo.com" };


        [TestMethod]
        public async Task Simple_BC_Create_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            mgr.ClearIdleCertificates();
            for (var i = 0; i < 5; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    // get the connection
                    var certificate = mgr.CreateCertificate(host, false);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());

            mgr.StopClearIdleCertificates();
        }

        // uncomment this to compare WinCert maker performance with BC (BC takes more time for same test above)
        //[TestMethod]
        public async Task Simple_Create_Win_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                { CertificateEngine = CertificateEngine.DefaultWindows };

            mgr.CreateRootCertificate();
            mgr.TrustRootCertificate(true);
            mgr.ClearIdleCertificates();

            for (var i = 0; i < 5; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    // get the connection
                    var certificate = mgr.CreateCertificate(host, false);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());
            mgr.RemoveTrustedRootCertificate(true);
            mgr.StopClearIdleCertificates();
        }

        [TestMethod]
        public async Task Create_Server_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                { CertificateEngine = CertificateEngine.BouncyCastleFast };

            mgr.SaveFakeCertificates = true;

            for (var i = 0; i < 500; i++)
                tasks.AddRange(hostNames.Select(host => Task.Run(() =>
                {
                    var certificate = mgr.CreateServerCertificate(host);
                    Assert.IsNotNull(certificate);
                })));

            await Task.WhenAll(tasks.ToArray());
        }

        /// <summary>
        /// Regression test for issue #965: issuer DN of generated leaf certificates must exactly
        /// match the subject DN of the custom signing root (preserving RDN order, C/O/L/CN attributes,
        /// and escaping) rather than being round-tripped through a display-string representation.
        /// Covers both BcCertificateMaker and BcCertificateMakerFast.
        /// </summary>
        [TestMethod]
        public void BC_Leaf_IssuerDN_Matches_Root_SubjectDN_RawBytes()
        {
            // Build a custom root certificate with multiple RDN attributes so that any string
            // round-trip would produce a different ordering or encoding.
            X509Certificate2 customRoot;
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest(
                    "C=AU, ST=Victoria, L=Melbourne, O=Acme Corp, OU=Proxy, CN=Acme Root CA",
                    rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                customRoot = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            }

            var expectedIssuerRaw = customRoot.SubjectName.RawData;

            // Test BcCertificateMaker
            var maker = new BcCertificateMaker(certificateValidDays: 365);
            var leaf = maker.MakeCertificate("example.com", customRoot);
            CollectionAssert.AreEqual(expectedIssuerRaw, leaf.IssuerName.RawData,
                "BcCertificateMaker: leaf IssuerName.RawData must equal signing root SubjectName.RawData");

            // Test BcCertificateMakerFast
            var makerFast = new BcCertificateMakerFast(certificateValidDays: 365);
            var leafFast = makerFast.MakeCertificate("example.com", customRoot);
            CollectionAssert.AreEqual(expectedIssuerRaw, leafFast.IssuerName.RawData,
                "BcCertificateMakerFast: leaf IssuerName.RawData must equal signing root SubjectName.RawData");

            customRoot.Dispose();
            leaf.Dispose();
            leafFast.Dispose();
        }

        [TestMethod]
        public async Task CreateServerCertificate_ExpiredCachedCertificate_IsRegenerated()
        {
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                { CertificateEngine = CertificateEngine.BouncyCastleFast };

            const string host = "expired.test";

            // build an already-expired self-signed certificate and inject it into the in-memory cache
            X509Certificate2 expiredCert;
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                expiredCert = request.CreateSelfSigned(
                    DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));
            }

            var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField);
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField.GetValue(mgr);
            cache[host] = new CachedCertificate(expiredCert) { LastAccess = DateTime.UtcNow };

            // capture before the call: the expired cert is evicted and disposed by the fix
            var expiredThumbprint = expiredCert.Thumbprint;

            var result = await mgr.CreateServerCertificate(host);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.NotAfter > DateTime.Now, "regenerated certificate should be valid");
            Assert.AreNotEqual(expiredThumbprint, result.Thumbprint,
                "expired cached certificate should have been replaced");
        }
    }
}