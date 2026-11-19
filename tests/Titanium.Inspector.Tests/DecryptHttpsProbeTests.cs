using System.Net.Http;
using System.Security.Authentication;
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
        Assert.IsFalse(result.Inconclusive);
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
        Assert.IsFalse(result.Inconclusive);
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

    [TestMethod]
    public void IsTlsAuthFailure_DetectsWrappedAuthenticationException()
    {
        var inner = new AuthenticationException("leaf not trusted");
        var wrapped = new HttpRequestException("SSL failed", inner);
        Assert.IsTrue(DecryptHttpsProbe.IsTlsAuthFailure(wrapped));
        Assert.IsFalse(DecryptHttpsProbe.IsTlsAuthFailure(new HttpRequestException("connection refused")));
        Assert.IsFalse(DecryptHttpsProbe.IsTlsAuthFailure(null));
    }

    [TestMethod]
    public void Result_Inconclusive_KeepsOkFalse()
    {
        var inconclusive = new DecryptHttpsProbe.Result(false, "Could not verify; example.com unreachable", Inconclusive: true);
        Assert.IsFalse(inconclusive.Ok);
        Assert.IsTrue(inconclusive.Inconclusive);
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
