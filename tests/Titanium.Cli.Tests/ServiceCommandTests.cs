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
        StringAssert.Contains(unit, "ExecReload=/bin/kill -HUP $MAINPID");
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

    [TestMethod]
    public void ResolveProgramPrefix_PrefersApphostWhenHostedByDotnet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var muxer = Path.Combine(dir, "dotnet");
            var dll = Path.Combine(dir, "titanium.dll");
            var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "titanium.exe" : "titanium");
            File.WriteAllText(muxer, "x");
            File.WriteAllText(dll, "x");
            File.WriteAllText(apphost, "x");
            var prefix = ServiceDefaults.ResolveProgramPrefix(muxer, [dll], dir);
            Assert.AreEqual(1, prefix.Length);
            Assert.AreEqual(Path.GetFullPath(apphost), prefix[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveProgramPrefix_FallsBackToDotnetDll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var muxer = Path.Combine(dir, "dotnet");
            var dll = Path.Combine(dir, "titanium.dll");
            File.WriteAllText(muxer, "x");
            File.WriteAllText(dll, "x");
            var prefix = ServiceDefaults.ResolveProgramPrefix(muxer, [dll], dir);
            CollectionAssert.AreEqual(
                new[] { Path.GetFullPath(muxer), Path.GetFullPath(dll) },
                prefix);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveProgramPrefix_NativeHostUnchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var host = Path.Combine(dir, "titanium");
            File.WriteAllText(host, "x");
            var prefix = ServiceDefaults.ResolveProgramPrefix(host, [host], dir);
            Assert.AreEqual(1, prefix.Length);
            Assert.AreEqual(Path.GetFullPath(host), prefix[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void BuildLaunchdPlist_IncludesHostedDllAndEnvironment()
    {
        var plist = ServiceUnitFactory.BuildLaunchdPlist(
            "com.justcoding121.titanium",
            ["/opt/dotnet/dotnet", "/opt/titanium/titanium.dll"],
            "/etc/titanium/twp.yaml",
            "/etc/titanium",
            "/Library/Logs/Titanium/titanium.out.log",
            "/Library/Logs/Titanium/titanium.err.log",
            new Dictionary<string, string> { ["DOTNET_ROOT"] = "/opt/dotnet" });
        StringAssert.Contains(plist, "<string>/opt/dotnet/dotnet</string>");
        StringAssert.Contains(plist, "<string>/opt/titanium/titanium.dll</string>");
        StringAssert.Contains(plist, "<key>EnvironmentVariables</key>");
        StringAssert.Contains(plist, "<key>DOTNET_ROOT</key>");
        StringAssert.Contains(plist, "<string>/opt/dotnet</string>");
    }

    [TestMethod]
    public void BuildWindowsBinPath_HostedByDotnet()
    {
        var bin = ServiceUnitFactory.BuildWindowsBinPath(
            [@"C:\Program Files\dotnet\dotnet.exe", @"C:\Titanium\titanium.dll"],
            @"C:\Titanium\twp.yaml",
            "titanium");
        StringAssert.StartsWith(bin, "\"C:\\Program Files\\dotnet\\dotnet.exe\" C:\\Titanium\\titanium.dll run -c");
    }

    [TestMethod]
    public void ResolveLaunchdPlistPath_UserHomeOverride()
    {
        Assert.AreEqual(
            "/Users/qa/Library/LaunchAgents/com.justcoding121.titanium.plist",
            ServiceUnitFactory.ResolveLaunchdPlistPath(
                "com.justcoding121.titanium", user: true, userHome: "/Users/qa"));
    }

    [TestMethod]
    public void ServicePayload_RemapPrefix_MovesAppDirOnly()
    {
        var src = Path.Combine(Path.GetTempPath(), "twp-src");
        var dst = Path.Combine(Path.GetTempPath(), "twp-dst");
        var muxer = Path.Combine(Path.GetTempPath(), "dotnet-fake", "dotnet");
        var remapped = ServicePayload.RemapPrefix(
            [muxer, Path.Combine(src, "titanium.dll")],
            src,
            dst);
        Assert.AreEqual(Path.GetFullPath(muxer), remapped[0]);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(dst, "titanium.dll")), remapped[1]);
    }

    [TestMethod]
    public void ServicePayload_DiscoverAppDirectory_FromDll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-app");
        var dll = Path.Combine(dir, "titanium.dll");
        Assert.AreEqual(
            Path.GetFullPath(dir),
            ServicePayload.DiscoverAppDirectory(["dotnet", dll]));
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

    [TestMethod]
    public async Task ServiceStatus_MissingName_AndCreateManager_DoNotHang()
    {
        _ = ServiceCommand.CreateManager();
        _ = ServiceCommand.PrintHelp();
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("install"));
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("uninstall"));
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("start"));
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("stop"));
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("restart"));
        Assert.AreEqual(0, ServiceCommand.PrintSubHelp("status"));
        Assert.AreEqual(1, ServiceCommand.PrintSubHelp("nope"));

        var status = await ServiceCommand.ExecuteAsync(
            ["service", "status", "--name", "titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8]]);
        Assert.IsTrue(status is 0 or 1, status.ToString());
        _ = await ServiceCommand.IsDefaultServiceRunningAsync();

        if (OperatingSystem.IsWindows())
        {
            var userInstall = await ServiceCommand.ExecuteAsync(
                ["service", "install", "-c", "missing.yaml", "--user"]);
            Assert.AreEqual(1, userInstall);
        }
    }

    [TestMethod]
    public async Task ServiceInstall_InvalidConfig_ExitsBeforeElevation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-svc-inv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, """
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 0
                type: ftp
            """);
        var previous = Environment.GetEnvironmentVariable(PrivilegePrompt.NoElevateEnv);
        try
        {
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, "1");
            var code = await ServiceCommand.ExecuteAsync(["service", "install", "-c", path, "--name", "titanium-qa-cov-inv"]);
            Assert.AreEqual(1, code);

            var start = await ServiceCommand.ExecuteAsync(
                ["service", "start", "--name", "titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8]]);
            Assert.IsTrue(start is 0 or 1, start.ToString());
            var stop = await ServiceCommand.ExecuteAsync(
                ["service", "stop", "--name", "titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8]]);
            Assert.IsTrue(stop is 0 or 1, stop.ToString());
            var restart = await ServiceCommand.ExecuteAsync(
                ["service", "restart", "--name", "titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8]]);
            Assert.IsTrue(restart is 0 or 1, restart.ToString());
            var uninstall = await ServiceCommand.ExecuteAsync(
                ["service", "uninstall", "--name", "titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8]]);
            Assert.IsTrue(uninstall is 0 or 1, uninstall.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, previous);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}

[TestClass]
public class PrivilegePromptTests
{
    private static readonly string[] ServiceInstallArgs = ["service", "install", "-c", "twp.yaml"];

    [TestCleanup]
    public void Reset() => PrivilegePrompt.ResetForTests();

    [TestMethod]
    public void TakeInternalArgs_StripsRelaunchFlags()
    {
        var rest = PrivilegePrompt.TakeInternalArgs([
            ..ServiceInstallArgs,
            PrivilegePrompt.RelaunchFlag,
            PrivilegePrompt.ParentPidFlag, "4242",
        ]);
        CollectionAssert.AreEqual(ServiceInstallArgs, rest);
        Assert.IsTrue(PrivilegePrompt.HasRelaunchFlag);
        Assert.AreEqual(4242u, PrivilegePrompt.ParentPid);
    }

    [TestMethod]
    public void AbsolutizeConfigArgs_ExpandsRelativeDashC()
    {
        var abs = PrivilegePrompt.AbsolutizeConfigArgs(ServiceInstallArgs);
        Assert.AreEqual("service", abs[0]);
        Assert.AreEqual("-c", abs[2]);
        Assert.IsTrue(Path.IsPathRooted(abs[3]), abs[3]);
        StringAssert.EndsWith(abs[3], "twp.yaml");
        _ = PrivilegePrompt.AbsolutizeConfigArgs(["run", "-c", "\0not-a-path"]);
    }

    [TestMethod]
    public void JoinWindowsArguments_QuotesSpaces()
    {
        var line = PrivilegePrompt.JoinWindowsArguments(["run", "-c", @"C:\My Config\twp.yaml"]);
        StringAssert.Contains(line, "\"C:\\My Config\\twp.yaml\"");
    }

    [TestMethod]
    public void FallbackAndPromptMessages_ArePlatformSpecific()
    {
        StringAssert.Contains(PrivilegePrompt.FallbackMessage(), "privileges");
        StringAssert.Contains(PrivilegePrompt.InteractivePromptMessage(), "permission");
    }

    [TestMethod]
    public void IsDotnetHostPath_DetectsHost()
    {
        Assert.IsTrue(ServiceDefaults.IsDotnetHostPath("dotnet"));
        Assert.IsTrue(ServiceDefaults.IsDotnetHostPath("dotnet.exe"));
        Assert.IsFalse(ServiceDefaults.IsDotnetHostPath("titanium.exe"));
        if (OperatingSystem.IsWindows())
            Assert.IsTrue(ServiceDefaults.IsDotnetHostPath(@"C:\Program Files\dotnet\dotnet.exe"));
        else
            Assert.IsTrue(ServiceDefaults.IsDotnetHostPath("/usr/bin/dotnet"));
    }

    [TestMethod]
    public async Task EnsureOrRelaunch_WhenAlreadyHandledOrNonInteractive_DoesNotLoop()
    {
        var result = await PrivilegePrompt.EnsureOrRelaunchAsync(["service", "status"]);
        if (PrivilegePrompt.IsElevated())
        {
            Assert.IsNull(result);
            return;
        }

        // Testhost redirects IO, so we must not pop UAC/sudo; caller prints the fallback.
        Assert.IsFalse(PrivilegePrompt.CanPromptInteractively());
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task EnsureOrRelaunch_WhenRelaunchFlagSet_ReturnsOne()
    {
        if (PrivilegePrompt.IsElevated())
        {
            Assert.Inconclusive("already elevated");
            return;
        }

        PrivilegePrompt.TakeInternalArgs([PrivilegePrompt.RelaunchFlag]);
        try
        {
            var code = await PrivilegePrompt.EnsureOrRelaunchAsync(["service", "status"]);
            Assert.AreEqual(1, code);
            PrivilegePrompt.TryAttachParentConsole();
            _ = PrivilegePrompt.AbsolutizeConfigArgs(["service", "install", "--config", "twp.yaml"]);
            _ = PrivilegePrompt.JoinWindowsArguments(["plain"]);
            _ = PrivilegePrompt.CanPromptInteractively();
            _ = PrivilegePrompt.ResolveSudoPath();
        }
        finally
        {
            PrivilegePrompt.ResetForTests();
        }
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
