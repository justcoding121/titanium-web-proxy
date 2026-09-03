using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UnixCertificateTrustTests
{
    [TestMethod]
    public void DetectLinuxNssPackage_PrefersAptThenDnfThenZypper()
    {
        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        var hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.IsNotNull(hint);
        Assert.AreEqual("libnss3-tools", hint!.Package);
        Assert.AreEqual("apt-get", hint.FileName);

        runner = new FakeProcessRunner();
        runner.When("sh", "command -v dnf", "/usr/bin/dnf");
        hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.AreEqual("nss-tools", hint!.Package);

        runner = new FakeProcessRunner();
        runner.When("sh", "command -v zypper", "/usr/bin/zypper");
        hint = UnixCertificateTrust.DetectLinuxNssPackage(runner);
        Assert.AreEqual("mozilla-nss-tools", hint!.Package);
    }

    [TestMethod]
    public void DetectLinuxNssPackage_WhenNoManager_ReturnsNull()
    {
        var runner = new FakeProcessRunner { DefaultSuccess = false };
        Assert.IsNull(UnixCertificateTrust.DetectLinuxNssPackage(runner));
    }

    [TestMethod]
    public void TryInstallNssCertutil_Linux_UsesElevationForAptPackage()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        // certutil remains missing (no When for command -v certutil)
        var elevation = new FakeElevationPrompt();
        var result = UnixCertificateTrust.TryInstallNssCertutil(runner, elevation);
        Assert.AreEqual(1, elevation.Calls.Count);
        Assert.IsTrue(elevation.Calls[0].Arguments.Contains("libnss3-tools", StringComparison.Ordinal));
        Assert.AreEqual(CertificateOsTrustKind.Failed, result.Kind);
    }

    [TestMethod]
    public void TryInstallNssCertutil_Linux_CancelElevation_ReturnsCancelled()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v apt-get", "/usr/bin/apt-get");
        var elevation = new FakeElevationPrompt { Cancel = true };
        var result = UnixCertificateTrust.TryInstallNssCertutil(runner, elevation);
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, result.Kind);
    }

    [TestMethod]
    public void CertificateOsTrustResult_OkAndFail_Helpers()
    {
        Assert.IsTrue(CertificateOsTrustResult.Ok().Succeeded);
        var fail = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.CertutilMissing, "missing", "libnss3-tools");
        Assert.IsFalse(fail.Succeeded);
        Assert.AreEqual("libnss3-tools", fail.PackageHint);
    }
}

[TestClass]
public class FirefoxCertificateTrustTests
{
    [TestMethod]
    public void ParseDefaultProfilePath_PrefersDefaultFlag()
    {
        var ini = """
            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/abcd.default-release
            Default=1

            [Profile1]
            Name=old
            IsRelative=1
            Path=Profiles/old.default
            """;
        var path = FirefoxCertificateTrust.ParseDefaultProfilePath(ini);
        Assert.AreEqual("Profiles/abcd.default-release", path);
    }

    [TestMethod]
    public void ParseDefaultProfilePath_FallsBackToFirstPath()
    {
        var ini = """
            [Profile0]
            Name=only
            IsRelative=1
            Path=Profiles/only.default
            """;
        var path = FirefoxCertificateTrust.ParseDefaultProfilePath(ini);
        Assert.AreEqual("Profiles/only.default", path);
    }

    [TestMethod]
    public void TryEnableWindowsEnterpriseRoots_OnWindows_WritesOrSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
            Assert.AreEqual(CertificateOsTrustKind.Unsupported, unsupported.Kind);
            return;
        }

        var result = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
        if (!result.Succeeded &&
            result.Message.Contains("Firefox profile", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("No Firefox profile on this machine");
            return;
        }

        Assert.IsTrue(result.Succeeded, result.Message);
        FirefoxCertificateTrust.TryClearWindowsEnterpriseRoots();
    }

    [TestMethod]
    public void GetFirefoxRoots_IncludesSnapAndFlatpakOnLinuxLayout()
    {
        var roots = FirefoxCertificateTrust.GetFirefoxRoots();
        Assert.IsTrue(roots.Length >= 1);
        if (!OperatingSystem.IsLinux())
            return;

        Assert.IsTrue(roots.Any(r => r.Contains(".mozilla", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(roots.Any(r => r.Contains("snap", StringComparison.OrdinalIgnoreCase) &&
                                     r.Contains("firefox", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(roots.Any(r => r.Contains(".var", StringComparison.Ordinal) &&
                                     r.Contains("org.mozilla.firefox", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TryRequestFirefoxQuit_WhenNotRunning_ReturnsTrue()
    {
        if (FirefoxCertificateTrust.IsFirefoxProcessRunning())
        {
            Assert.Inconclusive("Firefox is running on this machine");
            return;
        }

        Assert.IsTrue(FirefoxCertificateTrust.TryRequestFirefoxQuit(TimeSpan.FromMilliseconds(100)));
    }
}
