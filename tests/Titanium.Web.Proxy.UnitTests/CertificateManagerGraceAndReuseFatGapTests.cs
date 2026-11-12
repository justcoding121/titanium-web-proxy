#pragma warning disable TWP001
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: CertificateValidDays lifetime, in-memory cache
///     reuse for the same host, and distinct leaves for different hosts.
/// </summary>
[TestClass]
public class CertificateManagerGraceAndReuseFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
            SaveFakeCertificates = false,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task CertificateValidDays_ReflectsOnLeafNotAfter()
    {
        const int validDays = 30;
        using var mgr = NewMgr();
        mgr.CertificateValidDays = validDays;
        mgr.CertificateGraceDays = 0;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("valid-days-gap.example");
        Assert.IsNotNull(leaf);
        var span = leaf!.NotAfter - leaf.NotBefore;
        Assert.IsTrue(span.TotalDays >= validDays - 1 && span.TotalDays <= validDays + 1,
            $"Expected ~{validDays} days; got {span.TotalDays}");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SameHostTwice_ReturnsCachedThumbprint()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        const string host = "cache-reuse-gap.example";
        using var first = await mgr.CreateServerCertificate(host);
        using var second = await mgr.CreateServerCertificate(host);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first!.Thumbprint, second!.Thumbprint);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task DifferentHosts_ReturnDistinctThumbprints()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var a = await mgr.CreateServerCertificate("host-a-gap.example");
        using var b = await mgr.CreateServerCertificate("host-b-gap.example");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a!.Thumbprint, b!.Thumbprint);
    }
}
#pragma warning restore TWP001
