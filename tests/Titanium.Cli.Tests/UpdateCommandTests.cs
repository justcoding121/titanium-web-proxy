using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Updates;

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
    public void StripPrerelease_RemovesSuffix()
    {
        Assert.AreEqual("7.0.3", VersionCommand.StripPrerelease("v7.0.3-beta"));
        Assert.AreEqual("7.0.3", VersionCommand.StripPrerelease("7.0.3"));
    }

    [TestMethod]
    public void CliHelper_WindowsScript_ExtractsZip()
    {
        var script = CliUpdateApplyHelper.BuildWindowsScript(
            7,
            @"C:\temp\cli.zip",
            @"C:\tools\titanium",
            @"C:\tools\titanium\titanium.exe",
            "7.0.3",
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
            "7.0.3-beta",
            "beta");
        StringAssert.Contains(script, "unzip");
        StringAssert.Contains(script, "beta");
    }
}
