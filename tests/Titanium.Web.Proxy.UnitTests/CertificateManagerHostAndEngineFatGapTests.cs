#pragma warning disable TWP001
using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager host / engine edges: IP SAN, wildcard disable, ECDSA fast engine,
///     cache timeout properties, and EnsureRoot after ClearRoot.
/// </summary>
[TestClass]
public class CertificateManagerHostAndEngineFatGapTests
{
    private static CertificateManager NewMgr(CertificateEngine engine = CertificateEngine.BouncyCastle) =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = engine,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_IpLiteral_HasIpSan()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("127.0.0.1");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()
                  ?? leaf.Extensions.Cast<X509Extension>()
                      .Select(e => { try { return new X509SubjectAlternativeNameExtension(e.RawData, false); } catch { return null; } })
                      .FirstOrDefault(e => e is not null);
        Assert.IsTrue(
            leaf.Subject.Contains("127.0.0.1", StringComparison.Ordinal)
            || (san?.EnumerateIPAddresses().Any(ip => ip.ToString() == "127.0.0.1") ?? false)
            || leaf.GetNameInfo(X509NameType.DnsName, false).Contains("127.0.0.1", StringComparison.Ordinal),
            "IP leaf must carry the IP in subject or SAN.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task DisableWildCardCertificates_DoesNotIssueWildcardSubject()
    {
        using var mgr = NewMgr();
        mgr.DisableWildCardCertificates = true;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("a.b.example.test");
        Assert.IsNotNull(leaf);
        Assert.IsFalse(leaf!.Subject.Contains("*.", StringComparison.Ordinal),
            $"DisableWildCardCertificates must not mint wildcard subject; got {leaf.Subject}");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task BouncyCastleFast_Ecdsa_IssuesLeafWithPrivateKey()
    {
        using var mgr = NewMgr(CertificateEngine.BouncyCastleFast);
        mgr.ApplyFastColdStartLeafSettings();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ecdsa-fast-host.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        Assert.AreEqual(CertificateEngine.BouncyCastleFast, mgr.CertificateEngine);
    }

    [TestMethod]
    public void CertificateCacheTimeoutAndOverwrite_RoundTrip()
    {
        using var mgr = NewMgr();
        mgr.CertificateCacheTimeOutMinutes = 7;
        mgr.OverwritePfxFile = false;
        mgr.SaveFakeCertificates = true;
        Assert.AreEqual(7, mgr.CertificateCacheTimeOutMinutes);
        Assert.IsFalse(mgr.OverwritePfxFile);
        Assert.IsTrue(mgr.SaveFakeCertificates);
        mgr.SaveFakeCertificates = false;
        Assert.IsFalse(mgr.SaveFakeCertificates);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EnsureRoot_AfterClearRoot_CreatesUsableRootAndLeaf()
    {
        using var mgr = NewMgr();
        mgr.EnsureRootCertificate(false, false, false);
        Assert.IsNotNull(mgr.RootCertificate);
        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);
        mgr.EnsureRootCertificate(false, false, false);
        Assert.IsNotNull(mgr.RootCertificate);
        using var leaf = await mgr.CreateServerCertificate("after-ensure.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateCertificate_SyncPath_IssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = mgr.CreateCertificate("sync-path.example", false);
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
        await Task.CompletedTask;
    }
}
#pragma warning restore TWP001
