using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
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

            // NotBefore must be close to now - graceDays (allow ?1 minute for test execution lag)
            var expectedNotBefore = DateTime.UtcNow.AddDays(-graceDays);
            Assert.IsTrue(
                Math.Abs((cert.NotBefore.ToUniversalTime() - expectedNotBefore).TotalMinutes) < 2,
                $"NotBefore {cert.NotBefore:u} should be ~{expectedNotBefore:u}");

            cert.Dispose();
        }

        /// <summary>
        /// RSA leaf keys come from a background-refilled buffer (LeafKeyPairSource) so their cost is not
        /// paid on the CONNECT that needs the certificate. Buffering must not turn into sharing: the
        /// default engine's contract is a distinct key per host, and only BouncyCastleFast trades that away.
        /// </summary>
        [TestMethod]
        public void BC_Default_Engine_Issues_A_Distinct_Key_Per_Certificate()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };

            var publicKeys = new List<string>();
            foreach (var host in hostNames)
            {
                var cert = mgr.CreateCertificate(host, false);
                Assert.IsNotNull(cert, $"No certificate produced for {host}");
                publicKeys.Add(Convert.ToBase64String(cert.PublicKey.EncodedKeyValue.RawData));
                cert.Dispose();
            }

            CollectionAssert.AllItemsAreUnique(publicKeys,
                "Every leaf certificate from the default engine must carry its own key pair.");
        }

        /// <summary>
        /// The RSA key-pair buffer size is process-wide; setting it through CertificateManager must
        /// round-trip, reject out-of-range values, and still produce usable certificates when disabled.
        /// </summary>
        [TestMethod]
        public void LeafRsaKeyPairBufferSize_Is_Configurable()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };

            var previous = CertificateManager.LeafRsaKeyPairBufferSize;
            try
            {
                Assert.AreEqual(8, previous);

                CertificateManager.LeafRsaKeyPairBufferSize = 16;
                Assert.AreEqual(16, CertificateManager.LeafRsaKeyPairBufferSize);

                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CertificateManager.LeafRsaKeyPairBufferSize = -1);
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CertificateManager.LeafRsaKeyPairBufferSize = 257);

                CertificateManager.LeafRsaKeyPairBufferSize = 0;
                var cert = mgr.CreateCertificate("buffer-disabled.example", false);
                Assert.IsNotNull(cert);
                Assert.IsTrue(cert.HasPrivateKey);
                cert.Dispose();
            }
            finally
            {
                CertificateManager.LeafRsaKeyPairBufferSize = previous;
            }
        }

        /// <summary>
        /// P-256 leaves have to survive the round trip through the platform's key store, which is where
        /// they are easiest to get wrong: BouncyCastle will happily encode an EC private key with the
        /// curve spelled out instead of named, and Windows CNG rejects exactly that when the PKCS#12 blob
        /// is imported - the certificate is produced, then fails to load. Assert on a usable private key
        /// rather than merely on a certificate coming back.
        /// <para>
        /// Also locks down the documented contract that the root stays RSA while leaves are ECDSA, and
        /// that Fast+ECDSA must not fall through to a self-signed leaf when the root is minted.
        /// </para>
        /// </summary>
        [TestMethod]
        public void BC_Engines_Issue_Usable_EcdsaP256_Leaves()
        {
            foreach (var engine in new[] { CertificateEngine.BouncyCastle, CertificateEngine.BouncyCastleFast })
            {
                using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                {
                    CertificateEngine = engine,
                    LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256
                };

                Assert.IsTrue(mgr.CreateRootCertificate(false), $"{engine} must mint an RSA root for ECDSA leaves");
                var root = mgr.RootCertificate;
                Assert.IsNotNull(root);
                using (var rootRsa = root!.GetRSAPrivateKey())
                    Assert.IsNotNull(rootRsa, $"{engine} root must stay RSA when leaves are ECDSA");
                Assert.IsNull(root.GetECDsaPrivateKey(), $"{engine} root must not be ECDSA");

                using var cert = mgr.CreateCertificate(hostNames[0], false);

                Assert.IsNotNull(cert, $"No certificate produced by {engine}");
                Assert.AreEqual("1.2.840.10045.2.1", cert!.PublicKey.Oid.Value,
                    $"{engine} did not issue an EC leaf.");
                Assert.IsFalse(cert.SubjectName.RawData.SequenceEqual(cert.IssuerName.RawData),
                    $"{engine} must not fall through to a self-signed ECDSA leaf");
                CollectionAssert.AreEqual(root.SubjectName.RawData, cert.IssuerName.RawData,
                    $"{engine} leaf issuer DN must match the RSA root subject DN");

                using var ecdsa = cert.GetECDsaPrivateKey();
                Assert.IsNotNull(ecdsa, $"{engine} produced an EC leaf whose private key cannot be loaded.");
                Assert.AreEqual(256, ecdsa!.KeySize);
            }
        }

        [TestMethod]
        public void ApplyFastColdStartLeafSettings_Sets_Ecdsa_FastEngine_And_DiskCache()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance);
            Assert.AreEqual(CertificateKeyAlgorithm.Rsa2048, mgr.LeafCertificateKeyAlgorithm);
            Assert.IsFalse(mgr.SaveFakeCertificates);

            mgr.ApplyFastColdStartLeafSettings();

            Assert.AreEqual(CertificateEngine.BouncyCastleFast, mgr.CertificateEngine);
            Assert.AreEqual(CertificateKeyAlgorithm.EcdsaP256, mgr.LeafCertificateKeyAlgorithm);
            Assert.IsTrue(mgr.SaveFakeCertificates);
        }

        /// <summary>
        /// Regression test for issue #765: setting RootCertificate to the same certificate instance
        /// (same thumbprint) must NOT clear the in-memory leaf cache, so cached leaves survive a
        /// simulated restart where the same persisted root is reloaded.
        /// </summary>
        [TestMethod]
        public async Task RootCertificate_Reload_With_Same_Thumbprint_Preserves_Cache()
        {
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };

            mgr.CreateRootCertificate(false);
            var root = mgr.RootCertificate;
            Assert.IsNotNull(root);

            // Generate a leaf via CreateServerCertificate which populates the in-memory cache
            var leaf = await mgr.CreateServerCertificate("cache-reload.example");
            Assert.IsNotNull(leaf);
            var expectedThumbprint = leaf.Thumbprint;

            // Simulate restart: reassign the same root certificate (same thumbprint)
            mgr.RootCertificate = root;

            // The in-memory cache should still contain the leaf
            var leafAfterReload = await mgr.CreateServerCertificate("cache-reload.example");
            Assert.IsNotNull(leafAfterReload);
            Assert.AreEqual(expectedThumbprint, leafAfterReload.Thumbprint,
                "Cached leaf cert should be reused when the same root is reloaded");
        }

        /// <summary>
        /// Regression test for issue #765 (rotation path): setting a DIFFERENT root certificate
        /// must clear the in-memory leaf cache so stale leaves are not served.
        /// </summary>
        [TestMethod]
        public async Task RootCertificate_Changed_Clears_Cache()
        {
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };

            mgr.CreateRootCertificate(false);
            var leaf = await mgr.CreateServerCertificate("cache-rotation.example");
            Assert.IsNotNull(leaf);
            var originalThumbprint = leaf.Thumbprint;

            // Create a second manager with a different root
            var mgr2 = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            mgr2.CreateRootCertificate(false);
            var newRoot = mgr2.RootCertificate;
            Assert.IsNotNull(newRoot);
            Assert.AreNotEqual(mgr.RootCertificate!.Thumbprint, newRoot.Thumbprint);

            mgr.RootCertificate = newRoot;

            // Cache must have been cleared; fresh leaf (different thumbprint) is created
            var newLeaf = await mgr.CreateServerCertificate("cache-rotation.example");
            Assert.IsNotNull(newLeaf);
            Assert.AreNotEqual(originalThumbprint, newLeaf.Thumbprint,
                "Leaf cache must be invalidated when the signing root changes");
        }

        /// <summary>
        /// Regression test for issue #729: ProxyServer.Start must not call EnsureRootCertificate
        /// when all configured endpoints have DecryptSsl=false or supply a GenericCertificate.
        /// Verified by confirming that CertificateManager.RootCertificate remains null when no
        /// endpoint needs TLS decryption.
        /// </summary>
        [TestMethod]
        public void CertificateManager_EnsureRoot_OnlyCreatedWhenNeeded()
        {
            // Manager with no explicit root; root should NOT be auto-created when not needed
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };

            // RootCertificate must be null before any creation call
            Assert.IsNull(mgr.RootCertificate,
                "RootCertificate must be null before EnsureRootCertificate is called");

            // After creating it explicitly it should be set
            mgr.CreateRootCertificate(false);
            Assert.IsNotNull(mgr.RootCertificate,
                "RootCertificate must be set after CreateRootCertificate");
        }

        [TestMethod]
        public async Task Simple_BC_Create_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastle
            };
            _ = mgr.ClearIdleCertificates();
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
        public static async Task Simple_Create_Win_Certificate_Test()
        {
            var tasks = new List<Task>();

            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                { CertificateEngine = CertificateEngine.DefaultWindows };

            mgr.CreateRootCertificate();
            mgr.TrustRootCertificate(true);
            _ = mgr.ClearIdleCertificates();

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
            // Build root CA ? intermediate CA ? leaf chain
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
            // Chain should contain: leaf ? intermediate ? root (3 elements)
            Assert.IsTrue(chain.ChainElements.Count >= 2,
                "Certificate chain should contain at least leaf and intermediate");

            rootCa.Dispose();
            intermediateCa.Dispose();
            leaf.Dispose();
        }

        /// <summary>
        /// Characterization for issue #776: first manager persists a root PFX; a second manager lifetime
        /// loads that PFX and must still mint usable leaf certificates (the "examples fail on second run" case).
        /// </summary>
        [TestMethod]
        public void TwoManagerLifetimes_LoadSamePfx_CanIssueLeafCertificates()
        {
            var pfxPath = Path.Combine(Path.GetTempPath(), $"twp-lifetime-{Guid.NewGuid():N}.pfx");
            const string password = "";

            try
            {
                string rootThumbprint;
                using (var first = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                       {
                           CertificateEngine = CertificateEngine.BouncyCastle
                       })
                {
                    Assert.IsTrue(first.CreateRootCertificate(false));
                    Assert.IsNotNull(first.RootCertificate);
                    rootThumbprint = first.RootCertificate.Thumbprint;
                    File.WriteAllBytes(pfxPath,
                        first.RootCertificate.Export(X509ContentType.Pkcs12, password));
                }

                using var second = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                {
                    CertificateEngine = CertificateEngine.BouncyCastle
                };
                Assert.IsTrue(second.LoadRootCertificate(pfxPath, password, overwritePfXFile: false));
                Assert.IsNotNull(second.RootCertificate);
                Assert.AreEqual(rootThumbprint, second.RootCertificate.Thumbprint);

                var leaf = second.CreateCertificate("second-run.example", false);
                Assert.IsNotNull(leaf);
                Assert.IsTrue(leaf.HasPrivateKey);
                Assert.IsTrue(leaf.NotAfter > DateTime.Now);
                leaf.Dispose();
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
        /// Regression test for issue #923: BouncyCastle makers must produce a usable private-key
        /// certificate on every platform. On non-Windows this uses CopyWithPrivateKey (avoiding the
        /// macOS PKCS#12 Exportable import failure); on Windows the PKCS#12 Exportable path is preserved
        /// so disk-cache export continues to work.
        /// </summary>
        [DataTestMethod]
        [DataRow(CertificateEngine.BouncyCastle)]
        [DataRow(CertificateEngine.BouncyCastleFast)]
        public void BC_Leaf_HasPrivateKey_And_IsSslUsable_OnCurrentPlatform(CertificateEngine engineType)
        {
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = engineType
            };
            mgr.CreateRootCertificate(false);

            var leaf = mgr.CreateCertificate("macos-exportable.example", false);
            Assert.IsNotNull(leaf);
            Assert.IsTrue(leaf.HasPrivateKey, "Generated leaf must have a private key");

            using var rsa = leaf.GetRSAPrivateKey();
            Assert.IsNotNull(rsa, "Leaf private key must be accessible as RSA");

            // SslStreamCertificateContext construction is the practical usability gate for MITM.
            var ctx = mgr.CreateSslCertificateContext(leaf);
            Assert.IsNotNull(ctx);

            if (RunTime.IsWindows)
            {
                // Windows path must remain PKCS#12-exportable for DefaultCertificateDiskCache.
                var exported = leaf.Export(X509ContentType.Pkcs12);
                Assert.IsTrue(exported.Length > 0, "Windows leaf must remain PKCS#12 exportable");
            }

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
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField!.GetValue(mgr)!;
            cache[host] = new CachedCertificate(expiredCert) { LastAccess = DateTime.UtcNow };

            // capture before the call: the expired cert is evicted and disposed by the fix
            var expiredThumbprint = expiredCert.Thumbprint;

            var result = await mgr.CreateServerCertificate(host);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.NotAfter > DateTime.Now, "regenerated certificate should be valid");
            Assert.AreNotEqual(expiredThumbprint, result.Thumbprint,
                "expired cached certificate should have been replaced");
        }

        /// <summary>
        ///     Phase E.14 ("Bound in-memory... certificate caches"): the in-memory leaf-certificate
        ///     cache must never grow past the configured bound, evicting the least-recently-used entry
        ///     first, regardless of how many distinct hostnames are requested.
        /// </summary>
        [TestMethod]
        public async Task CertificateCache_EnforcesMaxEntries_EvictsLeastRecentlyUsed()
        {
            const int maxEntries = 2;
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance,
                () => maxEntries)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };

            await mgr.CreateServerCertificate("bound-a.example");
            await mgr.CreateServerCertificate("bound-b.example");
            await mgr.CreateServerCertificate("bound-c.example");

            var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField);
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField.GetValue(mgr)!;

            Assert.IsTrue(cache.Count <= maxEntries,
                $"in-memory certificate cache must never exceed the configured bound of {maxEntries}, had {cache.Count}");
            Assert.IsFalse(cache.ContainsKey("bound-a.example"),
                "the least-recently-used entry should have been evicted first");
        }

        /// <summary>
        ///     Regression: after a leaf is evicted (and disposed), reloading the same PKCS#12 yields a
        ///     new X509Certificate2 with the same thumbprint. SslStreamCertificateContext must not be
        ///     reused across instances — that yields CryptographicException "m_safeCertContext is an
        ///     invalid handle" on AuthenticateAsServerAsync and breaks MITM for that host permanently.
        /// </summary>
        [TestMethod]
        public async Task CreateSslCertificateContext_AfterEvictAndReload_DoesNotReuseDisposedLeafContext()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance,
                () => 1)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            var leaf1 = await mgr.CreateServerCertificate("reload-ctx.example");
            Assert.IsNotNull(leaf1);
            var pkcs12 = leaf1!.Export(X509ContentType.Pkcs12);
            var thumbprint = leaf1.Thumbprint;
            var ctx1 = mgr.CreateSslCertificateContext(leaf1);
            Assert.AreSame(leaf1, ctx1.TargetCertificate);

            // Bound=1: creating another host evicts reload-ctx.example and invalidates its context.
            var other = await mgr.CreateServerCertificate("other-evict.example");
            Assert.IsNotNull(other);

            var contextsField = typeof(CertificateManager).GetField("sslCertificateContexts",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(contextsField);
            var contexts = contextsField!.GetValue(mgr)!;
            var containsKey = contexts.GetType().GetMethod("ContainsKey")!;
            Assert.IsFalse((bool)containsKey.Invoke(contexts, [thumbprint])!,
                "eviction must drop the SslStreamCertificateContext for the evicted leaf thumbprint");

            // Force deferred dispose of the evicted leaf (grace window already elapsed).
            var pendingField = typeof(CertificateManager).GetField("pendingDisposals",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var queue = pendingField!.GetValue(mgr)!;
            var queueType = queue.GetType();
            var tryDequeue = queueType.GetMethod("TryDequeue")!;
            var enqueue = queueType.GetMethod("Enqueue")!;
            var itemType = queueType.GetGenericArguments()[0];
            var evictedAt = itemType.GetProperty("EvictedAtUtc")
                ?? (MemberInfo?)itemType.GetField("EvictedAtUtc");
            Assert.IsNotNull(evictedAt);
            var requeued = new List<object>();
            while (true)
            {
                var args = new object?[] { null };
                if (!(bool)tryDequeue.Invoke(queue, args)!) break;
                var item = args[0]!;
                var past = DateTime.UtcNow.AddMinutes(-2);
                if (evictedAt is PropertyInfo pi)
                {
                    var boxed = item;
                    pi.SetValue(boxed, past);
                    item = boxed;
                }
                else
                    ((FieldInfo)evictedAt!).SetValue(item, past);
                requeued.Add(item);
            }

            foreach (var item in requeued)
                enqueue.Invoke(queue, [item]);

            typeof(CertificateManager).GetMethod("DisposePendingEvictions",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(mgr, null);

            // Same PKCS#12 bytes → same thumbprint, distinct SafeCertContext (Inspector disk reload path).
            using var leaf2 = CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
            Assert.AreEqual(thumbprint, leaf2.Thumbprint);
            Assert.AreNotSame(leaf1, leaf2);

            var ctx2 = mgr.CreateSslCertificateContext(leaf2);
            Assert.AreNotSame(ctx1, ctx2,
                "must not reuse SslStreamCertificateContext built for a disposed leaf instance");
            Assert.AreSame(leaf2, ctx2.TargetCertificate);

            using var ms = new MemoryStream();
            await using var ssl = new System.Net.Security.SslStream(ms, leaveInnerStreamOpen: true);
            var options = new System.Net.Security.SslServerAuthenticationOptions
            {
                ServerCertificateContext = ctx2,
                ClientCertificateRequired = false
            };
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await ssl.AuthenticateAsServerAsync(options, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: no peer connected.
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                Assert.Fail(
                    "SslStream must not see disposed cert handle after reload; got: " + ex.Message);
            }
            catch (IOException)
            {
                // Also acceptable: stream closed without a peer.
            }
        }

        /// <summary>
        ///     Regression: CertificateManager.Dispose() must drain cachedCertificates so that the
        ///     native CAPI/OpenSSL handle of each leaf cert is released promptly.
        /// </summary>
        [TestMethod]
        public async Task Dispose_ReleasesAllCachedCertificates()
        {
            var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
                { CertificateEngine = CertificateEngine.BouncyCastleFast };

            // Populate the in-memory cache with at least one leaf cert.
            var cert = await mgr.CreateServerCertificate("dispose-test.example.com");
            Assert.IsNotNull(cert, "pre-condition: cache should have been populated");

            var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "cachedCertificates field not found via reflection");
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField.GetValue(mgr)!;

            Assert.IsFalse(cache.IsEmpty, "pre-condition: cache must be non-empty before Dispose");

            mgr.Dispose();

            Assert.AreEqual(0, cache.Count,
                "Dispose() must drain cachedCertificates to release native handles promptly");

            // Double-dispose must be idempotent.
            mgr.Dispose();
        }

        [TestMethod]
        public async Task ClearIdleCertificates_FirstSweep_EvictsStaleEntries()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast,
                CertificateCacheTimeOutMinutes = 60
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            var cert = await mgr.CreateServerCertificate("idle-evict.example");
            Assert.IsNotNull(cert);

            var cacheField = typeof(CertificateManager).GetField("cachedCertificates",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField);
            var cache = (ConcurrentDictionary<string, CachedCertificate>)cacheField.GetValue(mgr)!;
            Assert.IsTrue(cache.TryGetValue("idle-evict.example", out var cached));
            cached!.LastAccess = DateTime.UtcNow.AddHours(-2);

            var sweep = mgr.ClearIdleCertificates();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (cache.ContainsKey("idle-evict.example") && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            mgr.StopClearIdleCertificates();
            await sweep;

            Assert.IsFalse(cache.ContainsKey("idle-evict.example"),
                "idle sweep should evict entries older than the timeout");
        }

        [TestMethod]
        public async Task ClearIdleCertificates_FirstSweep_DisposesPendingEvictionsAfterGrace()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance,
                () => 1)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));

            await mgr.CreateServerCertificate("pending-a.example");
            await mgr.CreateServerCertificate("pending-b.example");

            var pendingField = typeof(CertificateManager).GetField("pendingDisposals",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(pendingField);
            var queue = pendingField.GetValue(mgr)!;
            var queueType = queue.GetType();
            var tryDequeue = queueType.GetMethod("TryDequeue")!;
            var enqueue = queueType.GetMethod("Enqueue")!;
            var itemType = queueType.GetGenericArguments()[0];
            var evictedAtProp = itemType.GetProperty("EvictedAtUtc")
                ?? itemType.GetField("EvictedAtUtc") as MemberInfo;
            Assert.IsNotNull(evictedAtProp, "PendingCertificateDisposal should expose EvictedAtUtc");
            var initialCount = (int)queueType.GetProperty("Count")!.GetValue(queue)!;
            Assert.IsTrue(initialCount > 0, "pre-condition: bound enforcement should queue pending disposals");

            var requeued = new List<object>();
            while (true)
            {
                var args = new object?[] { null };
                if (!(bool)tryDequeue.Invoke(queue, args)!) break;
                var item = args[0]!;
                // record struct: boxed copy; rewrite EvictedAtUtc then re-enqueue
                var past = DateTime.UtcNow.AddMinutes(-2);
                if (evictedAtProp is PropertyInfo pi)
                {
                    var boxed = item;
                    pi.SetValue(boxed, past);
                    item = boxed;
                }
                else
                    ((FieldInfo)evictedAtProp!).SetValue(item, past);
                requeued.Add(item);
            }

            foreach (var item in requeued)
                enqueue.Invoke(queue, new[] { item });

            var sweep = mgr.ClearIdleCertificates();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((int)queueType.GetProperty("Count")!.GetValue(queue)! > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            mgr.StopClearIdleCertificates();
            await sweep;

            Assert.AreEqual(0, (int)queueType.GetProperty("Count")!.GetValue(queue)!,
                "sweep should dispose pending evictions past the grace window");
        }

        [TestMethod]
        public async Task ClearRootCertificate_DisposesPendingEvictionsImmediately()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance,
                () => 1)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            await mgr.CreateServerCertificate("clear-pending-a.example");
            await mgr.CreateServerCertificate("clear-pending-b.example");

            var pendingField = typeof(CertificateManager).GetField("pendingDisposals",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(pendingField);
            var queue = pendingField.GetValue(mgr)!;
            Assert.IsTrue((int)queue.GetType().GetProperty("Count")!.GetValue(queue)! > 0);

            mgr.ClearRootCertificate();

            Assert.AreEqual(0, (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!,
                "ClearRootCertificate should drain pending evictions immediately");
            Assert.IsNull(mgr.RootCertificate);
        }

        [TestMethod]
        public void RemoveTrustedRootCertificate_NullRoot_LogsWithoutThrowing()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast
            };
            mgr.RemoveTrustedRootCertificate(machineTrusted: false);
            Assert.IsNull(mgr.RootCertificate);
        }

        /// <summary>
        /// Only DefaultWindows is rewritten off Windows; BouncyCastleFast must remain selectable
        /// on Linux/macOS (it is fully managed BouncyCastle).
        /// </summary>
        [TestMethod]
        public void CoerceEngineForPlatform_OnlyRewritesDefaultWindowsOffWindows()
        {
            Assert.AreEqual(CertificateEngine.BouncyCastle,
                CertificateManager.CoerceEngineForPlatform(CertificateEngine.DefaultWindows, isWindows: false));
            Assert.AreEqual(CertificateEngine.BouncyCastleFast,
                CertificateManager.CoerceEngineForPlatform(CertificateEngine.BouncyCastleFast, isWindows: false));
            Assert.AreEqual(CertificateEngine.BouncyCastle,
                CertificateManager.CoerceEngineForPlatform(CertificateEngine.BouncyCastle, isWindows: false));

            Assert.AreEqual(CertificateEngine.DefaultWindows,
                CertificateManager.CoerceEngineForPlatform(CertificateEngine.DefaultWindows, isWindows: true));
            Assert.AreEqual(CertificateEngine.BouncyCastleFast,
                CertificateManager.CoerceEngineForPlatform(CertificateEngine.BouncyCastleFast, isWindows: true));
        }

        /// <summary>
        /// On the current platform the setter must keep BouncyCastleFast (not silently downgrade it).
        /// </summary>
        [TestMethod]
        public void CertificateEngine_Setter_PreservesBouncyCastleFast()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance);
            mgr.CertificateEngine = CertificateEngine.BouncyCastleFast;
            Assert.AreEqual(CertificateEngine.BouncyCastleFast, mgr.CertificateEngine);
        }

        /// <summary>
        /// When root creation fails, CreateCertificate must return null rather than minting a
        /// self-signed leaf (the previous Fast+ECDSA fall-through).
        /// </summary>
        [TestMethod]
        public void CreateCertificate_WhenRootCreationFails_ReturnsNullNotSelfSignedLeaf()
        {
            var expiredRoot = CreateExpiredSelfSigned("expired-root-for-leaf");
            var cache = new RootOnlyExpiredCache(expiredRoot);

            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast,
                LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256,
                CertificateStorage = cache,
                OverwritePfxFile = false
            };

            var leaf = mgr.CreateCertificate("no-root-fallthrough.example", false);
            Assert.IsNull(leaf, "Must not return a self-signed leaf when the root cannot be created");
            Assert.IsNull(mgr.RootCertificate);
        }

        /// <summary>
        /// BC roots and leaves must advertise RFC-aligned KeyUsage (parity with WinCertificateMaker).
        /// </summary>
        [DataTestMethod]
        [DataRow(CertificateEngine.BouncyCastle)]
        [DataRow(CertificateEngine.BouncyCastleFast)]
        public void BC_Certificates_Include_RfcAligned_KeyUsage(CertificateEngine engineType)
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = engineType
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            var root = mgr.RootCertificate!;
            var rootKu = root.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            Assert.IsNotNull(rootKu, "Root must include KeyUsage");
            Assert.AreEqual(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, rootKu!.KeyUsages);

            using var rsaLeaf = mgr.CreateCertificate("ku-rsa.example", false);
            Assert.IsNotNull(rsaLeaf);
            var rsaKu = rsaLeaf!.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            Assert.IsNotNull(rsaKu, "RSA leaf must include KeyUsage");
            Assert.AreEqual(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                rsaKu!.KeyUsages);

            mgr.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256;
            using var ecLeaf = mgr.CreateCertificate("ku-ecdsa.example", false);
            Assert.IsNotNull(ecLeaf);
            var ecKu = ecLeaf!.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            Assert.IsNotNull(ecKu, "ECDSA leaf must include KeyUsage");
            Assert.AreEqual(X509KeyUsageFlags.DigitalSignature, ecKu!.KeyUsages);
            Assert.IsFalse(ecKu.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));
        }

        /// <summary>
        /// Custom ECDSA signing roots must work via SHA256WithECDSA (issuer-key helper).
        /// </summary>
        [TestMethod]
        public void BC_Makers_Sign_Leaves_With_Custom_Ecdsa_Root()
        {
            X509Certificate2 ecdsaRoot;
            using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var req = new CertificateRequest("CN=ECDSA Test Root", ecdsa, HashAlgorithmName.SHA256);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                ecdsaRoot = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            }

            try
            {
                var maker = new BcCertificateMaker(365, 2);
                using var leaf = maker.MakeCertificate("ecdsa-issuer.example", ecdsaRoot);
                CollectionAssert.AreEqual(ecdsaRoot.SubjectName.RawData, leaf.IssuerName.RawData);
                Assert.IsTrue(leaf.HasPrivateKey);

                var makerFast = new BcCertificateMakerFast(365, 2);
                using var leafFast = makerFast.MakeCertificate("ecdsa-issuer-fast.example", ecdsaRoot);
                CollectionAssert.AreEqual(ecdsaRoot.SubjectName.RawData, leafFast.IssuerName.RawData);
                Assert.IsTrue(leafFast.HasPrivateKey);
            }
            finally
            {
                ecdsaRoot.Dispose();
            }
        }

        /// <summary>
        /// Fast+ECDSA must keep sharing one leaf key across hosts while the RSA root uses a
        /// distinct key (the shared KeyPair must not leak into root generation).
        /// </summary>
        [TestMethod]
        public void BC_Fast_Ecdsa_Leaves_Share_Key_Distinct_From_Rsa_Root()
        {
            using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
            {
                CertificateEngine = CertificateEngine.BouncyCastleFast,
                LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256
            };
            Assert.IsTrue(mgr.CreateRootCertificate(false));
            var root = mgr.RootCertificate!;
            var rootKey = Convert.ToBase64String(root.PublicKey.EncodedKeyValue.RawData);

            var leafKeys = new List<string>();
            foreach (var host in hostNames)
            {
                using var leaf = mgr.CreateCertificate(host, false);
                Assert.IsNotNull(leaf);
                leafKeys.Add(Convert.ToBase64String(leaf!.PublicKey.EncodedKeyValue.RawData));
            }

            Assert.AreEqual(1, leafKeys.Distinct().Count(),
                "BouncyCastleFast must reuse one leaf key pair across hosts");
            Assert.AreNotEqual(rootKey, leafKeys[0],
                "RSA root key must not be the shared ECDSA leaf key");
        }

        /// <summary>
        /// Issuer-key helper must fail clearly when the signing certificate has no usable private key.
        /// </summary>
        [TestMethod]
        public void BcCertificateIssuer_Throws_When_SigningCert_Has_No_Private_Key()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=PublicOnly", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var withKey = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));
            // Strip the private key: export public cert bytes and reload.
            using var publicOnly = CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                BcCertificateIssuer.FromSigningCertificate(publicOnly));
            StringAssert.Contains(ex.Message, "neither an RSA nor an ECDSA");
        }

        private static X509Certificate2 CreateExpiredSelfSigned(string cn)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddDays(-1));
        }

        /// <summary>
        /// Minimal cache that only serves an expired root (for root-creation-failure paths).
        /// </summary>
        private sealed class RootOnlyExpiredCache : ICertificateCache
        {
            private readonly X509Certificate2 expiredRoot;

            public RootOnlyExpiredCache(X509Certificate2 expiredRoot) => this.expiredRoot = expiredRoot;

            public X509Certificate2? LoadRootCertificate(string pathOrName, string password,
                X509KeyStorageFlags storageFlags) =>
                // Return a fresh copy so callers can Dispose without breaking subsequent loads.
                CertificateLoader.LoadPkcs12(expiredRoot.Export(X509ContentType.Pkcs12), string.Empty,
                    storageFlags);

            public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
            {
            }

            public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags) => null;

            public void SaveCertificate(string subjectName, X509Certificate2 certificate)
            {
            }

            public void Clear()
            {
            }
        }
    }
}