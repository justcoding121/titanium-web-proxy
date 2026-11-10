using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Discovery;

namespace Titanium.Plus.Tests;

/// <summary>
///     File-backed k8s service-account token/CA helper.
///     Bug classes: TTL cache never reloads; custom-root TLS always fails; missing CA returns true.
/// </summary>
[TestClass]
public class ServiceAccountCredentialsBehaviorTests
{
    [TestMethod]
    public void GetToken_CachesUntilTtl_AndForceReload_AndSurvivesMissingFile()
    {
        var dir = Directory.CreateTempSubdirectory("twp-sa-").FullName;
        try
        {
            var tokenPath = Path.Combine(dir, "token");
            var caPath = Path.Combine(dir, "ca.crt");
            File.WriteAllText(tokenPath, " tok-a \n");
            var creds = new ServiceAccountCredentials(tokenPath, caPath, TimeSpan.FromMinutes(5));

            Assert.AreEqual("tok-a", creds.GetToken());
            File.WriteAllText(tokenPath, "tok-b");
            Assert.AreEqual("tok-a", creds.GetToken(), "within TTL the first token must stick");
            Assert.AreEqual("tok-b", creds.GetToken(forceReload: true));

            File.Delete(tokenPath);
            Assert.AreEqual("tok-b", creds.GetToken(forceReload: true), "IO miss must keep the previous token");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ValidateServerCertificate_AcceptsNoErrors_AndCustomRootTrust()
    {
        var dir = Directory.CreateTempSubdirectory("twp-sa-ca-").FullName;
        try
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootReq = new CertificateRequest("CN=twp-test-root", rootKey, HashAlgorithmName.SHA256);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var caPath = Path.Combine(dir, "ca.crt");
            File.WriteAllText(caPath, root.ExportCertificatePem());

            var creds = new ServiceAccountCredentials(Path.Combine(dir, "missing-token"), caPath, TimeSpan.FromMinutes(5));
            Assert.IsTrue(creds.ValidateServerCertificate(this, null, null, SslPolicyErrors.None));
            Assert.IsFalse(creds.ValidateServerCertificate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.IsFalse(creds.ValidateServerCertificate(
                this, root, null, SslPolicyErrors.RemoteCertificateNameMismatch));

            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafReq = new CertificateRequest("CN=leaf", leafKey, HashAlgorithmName.SHA256);
            var serial = RandomNumberGenerator.GetBytes(8);
            serial[0] &= 0x7f;
            using var leaf = leafReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
            Assert.IsTrue(creds.ValidateServerCertificate(
                this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
