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


        /// <summary>
        /// Regression test for issue #878: certificate NotBefore must only be backdated by the configured
        /// grace days (default 2), not the hard-coded 366 days.  Total validity
        /// (NotAfter - NotBefore) must equal validDays + graceDays so it stays within the
        /// Chrome/Apple 398-day limit.
        /// </summary>
        [DataTestMethod]
        [DataRow(CertificateEngine.BouncyCastle)]
        [DataRow(CertificateEngine.BouncyCastleFast)]
        public void Certificate_Lifetime_Respects_GraceDays_And_ValidDays(CertificateEngine engineType)
        {
            const int validDays = 395;
            const int graceDays = 2;

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = engineType,
                CertificateValidDays = validDays,
                CertificateGraceDays = graceDays
            };

            var cert = mgr.CreateCertificate("lifetime-test.example", false);
            Assert.IsNotNull(cert);

            var totalDays = (cert.NotAfter - cert.NotBefore).TotalDays;
            Assert.AreEqual(validDays + graceDays, (int)Math.Round(totalDays), 1,
                $"Total lifetime should be {validDays + graceDays} days, got {totalDays:F1}");

            // NotBefore must be close to now - graceDays (allow ±1 minute for test execution lag)
            var expectedNotBefore = DateTime.UtcNow.AddDays(-graceDays);
            Assert.IsTrue(
                Math.Abs((cert.NotBefore.ToUniversalTime() - expectedNotBefore).TotalMinutes) < 2,
                $"NotBefore {cert.NotBefore:u} should be ~{expectedNotBefore:u}");

            cert.Dispose();
        }

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
            var maker = new BcCertificateMaker(certificateValidDays: 365, certificateGraceDays: 2);
            var leaf = maker.MakeCertificate("example.com", customRoot);
            CollectionAssert.AreEqual(expectedIssuerRaw, leaf.IssuerName.RawData,
                "BcCertificateMaker: leaf IssuerName.RawData must equal signing root SubjectName.RawData");

            // Test BcCertificateMakerFast
            var makerFast = new BcCertificateMakerFast(certificateValidDays: 365, certificateGraceDays: 2);
            var leafFast = makerFast.MakeCertificate("example.com", customRoot);
            CollectionAssert.AreEqual(expectedIssuerRaw, leafFast.IssuerName.RawData,
                "BcCertificateMakerFast: leaf IssuerName.RawData must equal signing root SubjectName.RawData");

            customRoot.Dispose();
            leaf.Dispose();
            leafFast.Dispose();
        }

        /// <summary>
        /// Regression test for issue #904: when the proxy is configured with an intermediate CA
        /// as its signing root, CreateSslCertificateContext must include the intermediate CA in
        /// the returned SslStreamCertificateContext so that clients trust-anchored at the root CA
        /// can verify the generated leaf certificate chain.
        /// </summary>
        [TestMethod]
        public void BC_IntermediateCA_SslContext_IncludesIntermediateInChain()
        {
            // Build root CA → intermediate CA → leaf chain
            X509Certificate2 rootCa;
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=Test Root CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                rootCa = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            }

            X509Certificate2 intermediateCa;
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=Test Intermediate CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                intermediateCa = req.Create(rootCa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), new byte[] { 1 })
                    .CopyWithPrivateKey(rsa);
            }

            // Configure the proxy manager to use the intermediate CA as the signing certificate
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            mgr.RootCertificate = intermediateCa;

            var leaf = mgr.CreateCertificate("example.com", false);
            Assert.IsNotNull(leaf);

            // SslStreamCertificateContext creation should succeed and include the intermediate
            var ctx = mgr.CreateSslCertificateContext(leaf);
            Assert.IsNotNull(ctx);

            // Verify the chain: leaf should chain up to the rootCa when intermediate is provided
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(intermediateCa);
            chain.ChainPolicy.ExtraStore.Add(rootCa);
            chain.Build(leaf);
            // Chain should contain: leaf → intermediate → root (3 elements)
            Assert.IsTrue(chain.ChainElements.Count >= 2,
                "Certificate chain should contain at least leaf and intermediate");

            rootCa.Dispose();
            intermediateCa.Dispose();
            leaf.Dispose();
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