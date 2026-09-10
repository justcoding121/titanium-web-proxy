using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Updates;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Cli.Tests;

[TestClass]
public class UpdateCommandTests
{
    [TestMethod]
    public void ParseChannel_DefaultsStable()
    {
        Assert.AreEqual("stable", VersionCommand.ParseChannel([]));
    }

    [TestMethod]
    public void ParseChannel_ReadsFlag()
    {
        Assert.AreEqual("beta", VersionCommand.ParseChannel(["--channel", "Beta"]));
    }

    [TestMethod]
    public void TryResolveChannel_RejectsUnknown()
    {
        Assert.IsFalse(VersionCommand.TryResolveChannel(["--channel", "nightly"], out _, out var error));
        StringAssert.Contains(error, "Unknown channel");
    }

    [TestMethod]
    public void StripPrerelease_RemovesSuffix()
    {
        Assert.AreEqual("7.0.4", VersionCommand.StripPrerelease("v7.0.4-beta"));
        Assert.AreEqual("7.0.4", VersionCommand.StripPrerelease("7.0.4"));
    }

    [TestMethod]
    public void ReleaseVersion_ThreePartEqualsFourPartAssembly()
    {
        Assert.AreEqual(0, ReleaseVersion.Compare(new Version(7, 0, 5, 0), ReleaseVersion.ParseComparable("7.0.5")));
        Assert.AreEqual("7.0.5", ReleaseVersion.FormatDisplay(new Version(7, 0, 5, 0)));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverThreeVsFourPart_IsUpToDate()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", "7.0.5", "stable"));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_UpgradeWhenRemoteNewer()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.6", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_LocalNewer_DoesNotInstall()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 6, 0), "7.0.5", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverBetaOverStable_DoesNotInstall()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5", "stable"));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", null, null));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5-beta", "beta"));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_NewerBetaOverStable_Installs()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.6-beta", "beta", "7.0.5", "stable"));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverStableOverBeta_Installs()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", "7.0.5-beta", "beta"));
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", null, null, "7.0.5-beta"));
    }

    [TestMethod]
    public void ShouldInstallPlusRelease_SkipsEqualAndOlder_InstallsNewer()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(null, "7.0.5-beta"));
        Assert.IsFalse(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 5, 0), "7.0.5-beta"));
        Assert.IsFalse(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 6, 0), "7.0.5"));
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 5, 0), "7.0.6-beta"));
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(
            new Version(7, 0, 5, 0), "7.0.5", "7.0.5-beta"));
    }

    [TestMethod]
    public void ReleaseVersion_CompareReleaseTags_ReleaseBeatsSameCorePrerelease()
    {
        Assert.IsTrue(ReleaseVersion.CompareReleaseTags("7.0.5-beta", "7.0.5") < 0);
        Assert.IsTrue(ReleaseVersion.CompareReleaseTags("7.0.5", "7.0.5-beta") > 0);
        Assert.AreEqual(0, ReleaseVersion.CompareReleaseTags("7.0.5", "v7.0.5"));
        Assert.IsTrue(ReleaseVersion.IsRemoteNewer("7.0.5", "7.0.6-beta"));
        Assert.IsFalse(ReleaseVersion.IsRemoteNewer("7.0.5", "7.0.5-beta"));
        Assert.IsTrue(ReleaseVersion.IsPrereleaseTag("7.0.5-beta"));
        Assert.IsFalse(ReleaseVersion.IsPrereleaseTag("7.0.5"));
        Assert.AreEqual(
            "7.0.5-beta",
            ReleaseVersion.ResolveLocalReleaseLabel(new Version(7, 0, 5, 0), "7.0.5-beta+abc", null));
        Assert.AreEqual(
            "7.0.5",
            ReleaseVersion.ResolveLocalReleaseLabel(new Version(7, 0, 5, 0), null, null));
    }

    [TestMethod]
    public void CliHelper_WindowsScript_ExtractsZip()
    {
        var script = CliUpdateApplyHelper.BuildWindowsScript(
            7,
            @"C:\temp\cli.zip",
            @"C:\tools\titanium",
            @"C:\tools\titanium\titanium.exe",
            "7.0.4",
            "stable");
        StringAssert.Contains(script, "Expand-Archive");
        StringAssert.Contains(script, "7");
        StringAssert.Contains(script, "stable");
        StringAssert.Contains(script, "version");
    }

    [TestMethod]
    public void CliHelper_UnixScript_Unzips()
    {
        var script = CliUpdateApplyHelper.BuildUnixScript(
            3,
            "/tmp/cli.zip",
            "/opt/titanium",
            "/opt/titanium/titanium",
            "7.0.4-beta",
            "beta");
        StringAssert.Contains(script, "unzip");
        StringAssert.Contains(script, "beta");
    }

    [TestMethod]
    public void RemovePlus_DeletesDllBakAndStaging()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-remove-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dll = Path.Combine(dir, "Titanium.Plus.dll");
            File.WriteAllText(dll, "plus");
            File.WriteAllText(dll + ".bak", "bak");
            File.WriteAllText(dll + ".new", "new");

            Assert.AreEqual(0, UpdateCommand.RemovePlus(dir));
            Assert.IsFalse(File.Exists(dll));
            Assert.IsFalse(File.Exists(dll + ".bak"));
            Assert.IsFalse(File.Exists(dll + ".new"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void RemovePlus_IdempotentWhenAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-remove-plus-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.AreEqual(0, UpdateCommand.RemovePlus(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task Update_RejectsPlusAndRemovePlusTogether()
    {
        var code = await UpdateCommand.ExecuteAsync(["update", "--plus", "--remove-plus"]);
        Assert.AreEqual(1, code);
    }
}
