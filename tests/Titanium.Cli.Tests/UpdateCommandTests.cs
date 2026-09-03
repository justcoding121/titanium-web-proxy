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
    public void ShouldInstallCliRelease_SameSemverBetaSwitch()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5", "stable"));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5-beta", "beta"));
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
}
