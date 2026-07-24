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