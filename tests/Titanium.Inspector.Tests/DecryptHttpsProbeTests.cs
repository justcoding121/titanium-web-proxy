using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DecryptHttpsProbeTests
{
    [TestMethod]
    public async Task VerifyAsync_WhenProxyNotRunning_FailsFast()
    {
        using var interception = new InterceptionService();
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Message, "not listening");
    }

    [TestMethod]
    public async Task VerifyAsync_WhenDecryptOff_FailsFast()
    {
        using var interception = new InterceptionService();
        // BoundPort stays 0 until Start; still reports not listening first.
        interception.DecryptHttps = false;
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_NullLeaf_IsFalse()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        Assert.IsFalse(DecryptHttpsProbe.LeafChainsToMitmRoot(null, root));
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_RootAsLeaf_IsTrue()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        Assert.IsTrue(DecryptHttpsProbe.LeafChainsToMitmRoot(root, root));
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_UnrelatedLeaf_IsFalse()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        using var other = CreateSelfSigned("CN=Other Root");
        Assert.IsFalse(DecryptHttpsProbe.LeafChainsToMitmRoot(other, root));
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
