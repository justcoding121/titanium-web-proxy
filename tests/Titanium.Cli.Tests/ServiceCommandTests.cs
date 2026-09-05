using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Service;

namespace Titanium.Cli.Tests;

[TestClass]
public class ServiceUnitFactoryTests
{
    [TestMethod]
    public void BuildWindowsBinPath_QuotesSpaces()
    {
        var bin = ServiceUnitFactory.BuildWindowsBinPath(
            @"C:\Program Files\Titanium\titanium.exe",
            @"C:\Users\Me\My Config\twp.yaml",
            "titanium");
        StringAssert.Contains(bin, "\"C:\\Program Files\\Titanium\\titanium.exe\"");
        StringAssert.Contains(bin, "\"C:\\Users\\Me\\My Config\\twp.yaml\"");
        StringAssert.Contains(bin, "run -c");
        StringAssert.Contains(bin, "--service");
        StringAssert.Contains(bin, "--name titanium");
    }

    [TestMethod]
    public void BuildWindowsBinPath_NoQuotesWhenSimple()
    {
        var bin = ServiceUnitFactory.BuildWindowsBinPath(
            @"C:\Titanium\titanium.exe",
            @"C:\Titanium\twp.yaml",
            "titanium");
        Assert.IsFalse(bin.Contains('"'), bin);
        StringAssert.StartsWith(bin, @"C:\Titanium\titanium.exe run -c");
    }

    [TestMethod]
    public void BuildSystemdUnit_ContainsWorkingDirectoryAndRestart()
    {
        var unit = ServiceUnitFactory.BuildSystemdUnit(
            "/opt/titanium/titanium",
            "/etc/titanium/twp.yaml",
            "/etc/titanium",
            user: false);
        StringAssert.Contains(unit, "ExecStart=/opt/titanium/titanium run -c /etc/titanium/twp.yaml --service");
        StringAssert.Contains(unit, "WorkingDirectory=/etc/titanium");
        StringAssert.Contains(unit, "Restart=on-failure");
        StringAssert.Contains(unit, "WantedBy=multi-user.target");
    }

    [TestMethod]
    public void BuildSystemdUnit_User_WantedByDefaultTarget()
    {
        var unit = ServiceUnitFactory.BuildSystemdUnit(
            "/home/u/titanium",
            "/home/u/twp.yaml",
            "/home/u",
            user: true);
        StringAssert.Contains(unit, "WantedBy=default.target");
    }

    [TestMethod]
    public void BuildSystemdUnit_QuotesPathsWithSpaces()
    {
        var unit = ServiceUnitFactory.BuildSystemdUnit(
            "/opt/Titanium Web/titanium",
            "/etc/Titanium Web/twp.yaml",
            "/etc/Titanium Web",
            user: false);
        StringAssert.Contains(unit, "\"/opt/Titanium Web/titanium\"");
        StringAssert.Contains(unit, "\"/etc/Titanium Web/twp.yaml\"");
        StringAssert.Contains(unit, "WorkingDirectory=\"/etc/Titanium Web\"");
    }

    [TestMethod]
    public void BuildLaunchdPlist_ContainsProgramArgumentsAndKeepAlive()
    {
        var plist = ServiceUnitFactory.BuildLaunchdPlist(
            "com.justcoding121.titanium",
            "/usr/local/bin/titanium",
            "/etc/titanium/twp.yaml",
            "/etc/titanium",
            "/Library/Logs/Titanium/titanium.out.log",
            "/Library/Logs/Titanium/titanium.err.log");
        StringAssert.Contains(plist, "<string>com.justcoding121.titanium</string>");
        StringAssert.Contains(plist, "<string>/usr/local/bin/titanium</string>");
        StringAssert.Contains(plist, "<string>run</string>");
        StringAssert.Contains(plist, "<string>-c</string>");
        StringAssert.Contains(plist, "<string>/etc/titanium/twp.yaml</string>");
        StringAssert.Contains(plist, "<string>--service</string>");
        StringAssert.Contains(plist, "<key>KeepAlive</key>");
        StringAssert.Contains(plist, "<key>RunAtLoad</key>");
        StringAssert.Contains(plist, "<string>/etc/titanium</string>");
    }

    [TestMethod]
    public void ResolveSystemdUnitPath_SystemAndUser()
    {
        Assert.AreEqual(
            "/etc/systemd/system/titanium.service",
            ServiceUnitFactory.ResolveSystemdUnitPath("titanium", user: false));
        StringAssert.EndsWith(
            ServiceUnitFactory.ResolveSystemdUnitPath("myproxy", user: true),
            "/.config/systemd/user/myproxy.service");
    }

    [TestMethod]
    public void ResolveLaunchdPlistPath_SystemAndUser()
    {
        Assert.AreEqual(
            "/Library/LaunchDaemons/com.justcoding121.titanium.plist",
            ServiceUnitFactory.ResolveLaunchdPlistPath("com.justcoding121.titanium", user: false));
        StringAssert.Contains(
            ServiceUnitFactory.ResolveLaunchdPlistPath("com.justcoding121.titanium", user: true),
            "/Library/LaunchAgents/com.justcoding121.titanium.plist");
    }

    [TestMethod]
    public void ResolveMacOsLabel_PrefixesUnlessCom()
    {
        Assert.AreEqual("com.justcoding121.titanium", ServiceDefaults.ResolveMacOsLabel("titanium"));
        Assert.AreEqual("com.example.custom", ServiceDefaults.ResolveMacOsLabel("com.example.custom"));
    }
}

[TestClass]
public class ServiceCommandParseTests
{
    [TestMethod]
    public void ParseName_DefaultAndCustom()
    {
        Assert.AreEqual("titanium", ServiceCommand.ParseName(["service", "status"]));
        Assert.AreEqual("edge", ServiceCommand.ParseName(["service", "status", "--name", "edge"]));
    }

    [TestMethod]
    public void ParseUser_Flag()
    {
        Assert.IsFalse(ServiceCommand.ParseUser(["service", "install", "-c", "x.yaml"]));
        Assert.IsTrue(ServiceCommand.ParseUser(["service", "install", "-c", "x.yaml", "--user"]));
    }

    [TestMethod]
    public async Task ServiceHelp_Exit0()
    {
        var code = await ServiceCommand.ExecuteAsync(["service", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task ServiceInstallHelp_Exit0()
    {
        var code = await ServiceCommand.ExecuteAsync(["service", "install", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task ServiceUnknownSubcommand_Exit1()
    {
        var code = await ServiceCommand.ExecuteAsync(["service", "explode"]);
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public async Task ServiceInstall_MissingConfig_Exit1()
    {
        var code = await ServiceCommand.ExecuteAsync(["service", "install"]);
        Assert.AreEqual(1, code);
    }
}

[TestClass]
public class NestedHelpTests
{
    [TestMethod]
    public void CliHelp_DetectsTokens()
    {
        Assert.IsTrue(CliHelp.IsHelpToken("--help"));
        Assert.IsTrue(CliHelp.IsHelpToken("-h"));
        Assert.IsTrue(CliHelp.IsHelpToken("help"));
        Assert.IsFalse(CliHelp.IsHelpToken("-c"));
        Assert.IsTrue(CliHelp.RequestsHelp(new[] { "-c", "x", "--help" }));
        Assert.IsFalse(CliHelp.RequestsHelp(new[] { "-c", "x.yaml" }));
    }

    [TestMethod]
    public void RunCommand_PrintHelp_Exit0()
    {
        Assert.AreEqual(0, Config.RunCommand.PrintHelp());
    }

    [TestMethod]
    public void TestCommand_PrintHelp_Exit0()
    {
        Assert.AreEqual(0, Config.TestCommand.PrintHelp());
    }

    [TestMethod]
    public void VersionCommand_PrintHelp_Exit0()
    {
        Assert.AreEqual(0, Updates.VersionCommand.PrintHelp());
    }

    [TestMethod]
    public void UpdateCommand_PrintHelp_Exit0()
    {
        Assert.AreEqual(0, Updates.UpdateCommand.PrintHelp());
    }

    [TestMethod]
    public async Task Run_Help_DoesNotRequireConfig()
    {
        var code = await Config.RunCommand.ExecuteAsync(["run", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task Test_Help_DoesNotRequireConfig()
    {
        var code = await Config.TestCommand.ExecuteAsync(["test", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task Update_Help_DoesNotHitNetwork()
    {
        // Must return before UpdateFeedClient is constructed.
        var code = await Updates.UpdateCommand.ExecuteAsync(["update", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task Version_Help_Exit0()
    {
        var code = await Updates.VersionCommand.ExecuteAsync(["version", "--help"]);
        Assert.AreEqual(0, code);
    }
}
