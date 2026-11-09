#pragma warning disable TWP001
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: ApplyFastColdStartLeafSettings, PFX persist round-trip,
///     LoadRootCertificate, and ClearRoot after issue.
/// </summary>
[TestClass]
public class CertificateManagerColdStartAndPfxFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task ApplyFastColdStartLeafSettings_StillIssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.ApplyFastColdStartLeafSettings();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("cold-start.gap.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateRoot_PersistToTempPfx_LoadRoot_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-gap-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            using var mgr = NewMgr();
            mgr.PfxFilePath = path;
            mgr.PfxPassword = "gap-pfx";
            mgr.OverwritePfxFile = true;
            Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: true));
            Assert.IsTrue(File.Exists(path));

            using var loaded = NewMgr();
            Assert.IsTrue(loaded.LoadRootCertificate(path, "gap-pfx", overwritePfXFile: false));
            Assert.IsNotNull(loaded.RootCertificate);
            Assert.IsTrue(loaded.RootCertificate!.HasPrivateKey);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClearRoot_AfterIssue_AllowsNewRootAndLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var first = await mgr.CreateServerCertificate("before-clear.gap.test");
        Assert.IsNotNull(first);
        var firstThumb = mgr.RootCertificate!.Thumbprint;

        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);

        mgr.CreateRootCertificate(false);
        Assert.IsNotNull(mgr.RootCertificate);
        using var second = await mgr.CreateServerCertificate("after-clear.gap.test");
        Assert.IsNotNull(second);
        Assert.AreNotEqual(firstThumb, mgr.RootCertificate!.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void OcspStapling_AndIssuerName_RoundTrip()
    {
        using var mgr = NewMgr();
        mgr.OcspStapling = true;
        mgr.RootCertificateIssuerName = "Gap Issuer CN";
        mgr.RootCertificateName = "Gap Root CN";
        Assert.IsTrue(mgr.OcspStapling);
        Assert.AreEqual("Gap Issuer CN", mgr.RootCertificateIssuerName);
        Assert.AreEqual("Gap Root CN", mgr.RootCertificateName);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void EnsureRootCertificate_Idempotent()
    {
        using var mgr = NewMgr();
        mgr.EnsureRootCertificate();
        var thumb = mgr.RootCertificate!.Thumbprint;
        mgr.EnsureRootCertificate();
        Assert.AreEqual(thumb, mgr.RootCertificate!.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }
}
