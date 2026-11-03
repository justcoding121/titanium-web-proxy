using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CertificateStoreWriteSuppressionTests
{
    [TestMethod]
    public void SuppressedHost_DoesNotStageAnIntermediateIntoTheUserStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Windows certificate store is the leak this guards.");
            return;
        }

        Assert.IsTrue(CertificateManager.ShouldSuppressInteractiveRootStoreMutations);
        using var certificate = CreateCertificate();
        var before = Count(certificate.Thumbprint);
        var stage = typeof(CertificateManager).GetMethod(
            "StageIntermediateForOsChainBuild",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(stage);
        try
        {
            stage.Invoke(null, [certificate]);
            Assert.AreEqual(before, Count(certificate.Thumbprint));
        }
        finally
        {
            Remove(certificate.Thumbprint);
        }
    }

    [TestMethod]
    public void SuppressedHost_CreatingAServerCertificateContext_DoesNotGrowTheUserStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Windows certificate store is the leak this guards.");
            return;
        }

        string rootThumb;
        string leafThumb;
        using (var manager = new CertificateManager(
                   IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
               {
                   CertificateEngine = CertificateEngine.BouncyCastle,
               })
        {
            Assert.IsTrue(manager.CreateRootCertificate(persistToFile: false));
            var root = manager.RootCertificate;
            Assert.IsNotNull(root);
            rootThumb = root!.Thumbprint;
            using var leaf = manager.CreateServerCertificate("store-leak.example").GetAwaiter().GetResult();
            Assert.IsNotNull(leaf);
            leafThumb = leaf!.Thumbprint;
            _ = manager.CreateSslCertificateContext(leaf);
        }

        // SslStreamCertificateContext can keep a CA store copy alive until the manager (and its
        // cached contexts) are disposed. Assert after Dispose, by this fixture's thumbprints only.
        Assert.AreEqual(0, CountInStores(rootThumb), "Root must not remain in My/Root/CA after Dispose.");
        Assert.AreEqual(0, CountInStores(leafThumb), "Leaf must not remain in My/Root/CA after Dispose.");
    }

    private static int CountInStores(string thumbprint)
    {
        var count = 0;
        foreach (var name in new[] { StoreName.Root, StoreName.My, StoreName.CertificateAuthority })
        {
            using var store = new X509Store(name, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            count += store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count;
        }

        return count;
    }

    private static int Count(string thumbprint)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count;
    }

    private static void Remove(string thumbprint)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            store.Remove(cert);
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Titanium UnitTest Root CA " + Guid.NewGuid().ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
