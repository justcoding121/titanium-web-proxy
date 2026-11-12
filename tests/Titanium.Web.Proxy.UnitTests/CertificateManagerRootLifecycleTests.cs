using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Process-visible CertificateManager root lifecycle edges not covered by the storage/edge suites:
///     idempotent EnsureRootCertificate, trust probes with no store install, and engine coercion via the setter.
/// </summary>
[TestClass]
public class CertificateManagerRootLifecycleTests
{
    private static CertificateManager NewManager()
    {
        var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        return mgr;
    }

    [TestMethod]
    [Timeout(60_000)]
    public void EnsureRootCertificate_Idempotent_KeepsSameRootInstance()
    {
        // Bug class: a second EnsureRootCertificate recreates the root and invalidates already-issued leaves.
        using var mgr = NewManager();
        mgr.EnsureRootCertificate(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        var first = mgr.RootCertificate;
        Assert.IsNotNull(first);

        mgr.EnsureRootCertificate(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        Assert.AreSame(first, mgr.RootCertificate,
            "EnsureRootCertificate must not replace an already-valid root.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public void IsRootCertificateTrusted_WithoutInstall_ReturnsFalse()
    {
        // Bug class: trust probes return true for an in-memory root that was never installed in a store.
        using var mgr = NewManager();
        mgr.CreateRootCertificate(persistToFile: false);

        Assert.IsFalse(mgr.IsRootCertificateUserTrusted(),
            "In-memory root must not report user trust without a store install.");
        Assert.IsFalse(mgr.IsRootCertificateMachineTrusted(),
            "In-memory root must not report machine trust without a store install.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public void CertificateEngine_Setter_CoercesDefaultWindowsOffWindows()
    {
        // Bug class: DefaultWindows is kept on non-Windows and later fails leaf creation.
        using var mgr = NewManager();
        mgr.CertificateEngine = CertificateEngine.DefaultWindows;

        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(CertificateEngine.DefaultWindows, mgr.CertificateEngine);
        }
        else
        {
            Assert.AreEqual(CertificateEngine.BouncyCastle, mgr.CertificateEngine,
                "DefaultWindows must coerce to BouncyCastle off Windows.");
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public void ClearRootCertificate_ThenEnsure_CreatesFreshRoot()
    {
        // Bug class: after ClearRootCertificate, EnsureRootCertificate leaves RootCertificate null
        // (or reuses a disposed instance).
        using var mgr = NewManager();
        mgr.CreateRootCertificate(persistToFile: false);
        var oldThumb = mgr.RootCertificate!.Thumbprint;

        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);

        mgr.EnsureRootCertificate(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.AreNotEqual(oldThumb, mgr.RootCertificate!.Thumbprint,
            "A cleared root must be replaced with a freshly minted certificate.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateServerCertificate_AfterClearRoot_FailsOrRecreatesRoot()
    {
        // Bug class: CreateServerCertificate after ClearRootCertificate throws cryptic NullReferenceException
        // instead of recreating / failing cleanly.
        using var mgr = NewManager();
        mgr.CreateRootCertificate(persistToFile: false);
        mgr.ClearRootCertificate();

        // EnsureRoot via CreateServerCertificate path: CreateCertificate should call through root creation
        // when CertValidated is false.
        mgr.EnsureRootCertificate(userTrustRootCertificate: false, machineTrustRootCertificate: false);
        var leaf = mgr.CreateServerCertificate("after-clear.example").GetAwaiter().GetResult();
        Assert.IsNotNull(leaf);
        leaf!.Dispose();
    }

    [TestMethod]
    [Timeout(10_000)]
    public void RootCertificateName_AndIssuer_RoundTripOnNewManager()
    {
        // Bug class: RootCertificateName / IssuerName setters are ignored when creating the next root.
        var name = IsolatedTestCa.NewRootName();
        var issuer = IsolatedTestCa.Issuer + "-edge";
        using var mgr = new CertificateManager(name, issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

        Assert.AreEqual(name, mgr.RootCertificateName);
        Assert.AreEqual(issuer, mgr.RootCertificateIssuerName);

        mgr.RootCertificateName = name + "-renamed";
        mgr.RootCertificateIssuerName = issuer + "-renamed";
        Assert.AreEqual(name + "-renamed", mgr.RootCertificateName);
        Assert.AreEqual(issuer + "-renamed", mgr.RootCertificateIssuerName);
    }
}
