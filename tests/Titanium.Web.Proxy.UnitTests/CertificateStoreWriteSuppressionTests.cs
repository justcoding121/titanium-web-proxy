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

        var before = CountSubjects();
        using var manager = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(manager.CreateRootCertificate(persistToFile: false));
        using var leaf = manager.CreateServerCertificate("store-leak.example").GetAwaiter().GetResult();
        Assert.IsNotNull(leaf);
        _ = manager.CreateSslCertificateContext(leaf!);
        Assert.AreEqual(before, CountSubjects(), "A test host must not leave the root or leaf in a real store.");
    }

    private static int CountSubjects()
    {
        var count = 0;
        foreach (var name in new[] { StoreName.Root, StoreName.My, StoreName.CertificateAuthority })
        {
            using var store = new X509Store(name, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var cert in store.Certificates)
            {
                var text = cert.Subject + " " + cert.Issuer;
                if (text.Contains("Titanium", StringComparison.Ordinal) ||
                    text.Contains("twp-", StringComparison.Ordinal) ||
                    text.Contains("Acme Corp", StringComparison.Ordinal))
                {
                    count++;
                }
            }
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
