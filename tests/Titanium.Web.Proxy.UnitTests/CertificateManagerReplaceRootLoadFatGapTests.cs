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
///     Fat <see cref="CertificateManager" /> edges: <see cref="CertificateManager.LoadRootCertificate" />
///     failure paths and replacing the in-memory root via a second PFX load.
/// </summary>
[TestClass]
public class CertificateManagerReplaceRootLoadFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    /// <summary>
    ///     Bug class: wrong PFX password still assigns <see cref="CertificateManager.RootCertificate" />.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public void LoadRootCertificate_WrongPassword_ReturnsFalseAndNullRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave34-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            using (var writer = NewMgr())
            {
                writer.PfxFilePath = path;
                writer.PfxPassword = "good-pw";
                writer.OverwritePfxFile = true;
                Assert.IsTrue(writer.CreateRootCertificate(persistToFile: true));
            }

            using var loader = NewMgr();
            Assert.IsFalse(loader.LoadRootCertificate(path, "bad-pw", overwritePfXFile: false));
            Assert.IsNull(loader.RootCertificate);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    ///     Bug class: loading a replacement root PFX does not change the active root thumbprint.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task LoadRootCertificate_SecondPfx_ReplacesThumbprint_AndStillIssuesLeaf()
    {
        var pathA = Path.Combine(Path.GetTempPath(), "twp-a-" + Guid.NewGuid().ToString("N") + ".pfx");
        var pathB = Path.Combine(Path.GetTempPath(), "twp-b-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            string thumbA;
            using (var a = NewMgr())
            {
                a.PfxFilePath = pathA;
                a.PfxPassword = "pw-a";
                a.OverwritePfxFile = true;
                Assert.IsTrue(a.CreateRootCertificate(persistToFile: true));
                thumbA = a.RootCertificate!.Thumbprint;
            }

            using (var b = NewMgr())
            {
                b.PfxFilePath = pathB;
                b.PfxPassword = "pw-b";
                b.OverwritePfxFile = true;
                Assert.IsTrue(b.CreateRootCertificate(persistToFile: true));
            }

            using var mgr = NewMgr();
            Assert.IsTrue(mgr.LoadRootCertificate(pathA, "pw-a", overwritePfXFile: false));
            Assert.AreEqual(thumbA, mgr.RootCertificate!.Thumbprint, StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(mgr.LoadRootCertificate(pathB, "pw-b", overwritePfXFile: false));
            Assert.AreNotEqual(thumbA, mgr.RootCertificate!.Thumbprint, StringComparer.OrdinalIgnoreCase);
            using var leaf = await mgr.CreateServerCertificate("replace-root-wave34.test");
            Assert.IsNotNull(leaf);
            Assert.IsTrue(leaf!.HasPrivateKey);
        }
        finally
        {
            try { File.Delete(pathA); } catch { }
            try { File.Delete(pathB); } catch { }
        }
    }
}
#pragma warning restore TWP001
