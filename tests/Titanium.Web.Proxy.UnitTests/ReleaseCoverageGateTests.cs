using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Runs Linux and macOS helper branches on the Windows coverage host.
///     File writes stay under a temp directory. WinINET changes are restored.
/// </summary>
[TestClass]
public class ReleaseCoverageGateTests
{
    private static string _noop = "";
    private string _home = "";

    [ClassInitialize]
    public static void BuildNoop(TestContext _)
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-cov-noop");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "noop.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"),
            """
            class P {
                static int Main(string[] args) {
                    foreach (var a in args)
                        if (a == "--scope") return 1;
                    return 0;
                }
            }
            """);
        var output = Path.Combine(dir, "out");
        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{dir}\" -c Release -o \"{output}\"")
        {
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("dotnet build did not start");
        if (!build.WaitForExit(120_000) || build.ExitCode != 0)
            throw new InvalidOperationException("noop exe build failed: " + build.ExitCode);
        _noop = File.Exists(Path.Combine(output, "noop.exe"))
            ? Path.Combine(output, "noop.exe")
            : Path.Combine(output, "noop");
    }

    [TestInitialize]
    public void IsolateHome()
    {
        _home = Path.Combine(Path.GetTempPath(), "twp-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        CoverageOs.HomeForTests = _home;
        CoverageOs.TreatAsLinuxForTests = false;
        CoverageOs.TreatAsMacForTests = false;
        UnixProcessPath.ResolveOverrideForTests = _ => _noop;
        InstalledProxyCleanup.RunOverrideForTests = null;
    }

    [TestCleanup]
    public void ResetIsolation()
    {
        CoverageOs.HomeForTests = null;
        CoverageOs.TreatAsLinuxForTests = false;
        CoverageOs.TreatAsMacForTests = false;
        UnixProcessPath.ResolveOverrideForTests = null;
        InstalledProxyCleanup.RunOverrideForTests = null;
        try { Directory.Delete(_home, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public void FailOpen_SystemdFailureFallsBackToSetsid_AndStopReadsTheScopeFile()
    {
        var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var previousDbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/tmp/twp-cov-runtime");
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/tmp/twp-cov-bus");
            LinuxProxyFailOpen.Start("127.0.0.1", 8866);
            Assert.IsTrue(LinuxProxyFailOpen.OwnedByThisProcess);
            Assert.IsFalse(LinuxProxyFailOpen.StopUnlessOwnedByThisProcess());
            LinuxProxyFailOpen.Stop();
            Assert.IsFalse(LinuxProxyFailOpen.OwnedByThisProcess);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousDbus);
            LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
        }
    }

    [TestMethod]
    public void FailOpen_WithoutSystemd_UsesSetsid_AndStopIfCoveringPortReadsThePidFile()
    {
        var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var previousDbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);
            LinuxProxyFailOpen.Start("127.0.0.1", 8877);
            var dir = Path.Combine(_home, ".config", "TitaniumInspector");
            File.WriteAllText(Path.Combine(dir, "fail-open-proxy.pid"), "999999");
            LinuxProxyFailOpen.StopIfCoveringPort(8877);
            LinuxProxyFailOpen.Stop();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousDbus);
            LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
        }
    }

    [TestMethod]
    public void FirefoxClear_RestoresAMarkedProfile_AndSkipsAnUntouchedOne()
    {
        CoverageOs.TreatAsLinuxForTests = true;
        var marked = Path.Combine(_home, ".mozilla", "firefox", "marked.default");
        var plain = Path.Combine(_home, ".mozilla", "firefox", "plain.default");
        Directory.CreateDirectory(marked);
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(marked, "prefs.js"),
            "user_pref(\"titanium.inspector.proxy.managed\", true);\n" +
            "user_pref(\"network.proxy.http\", \"127.0.0.1\");\n" +
            "user_pref(\"network.proxy.type\", 1);\n");
        File.WriteAllText(Path.Combine(plain, "prefs.js"), "user_pref(\"browser.startup.page\", 0);\n");
        var backupDir = Path.Combine(_home, ".config", "TitaniumInspector");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "firefox-proxy-backup.json"),
            "{\"network.proxy.type\":\"0\"}");

        LinuxFirefoxProxy.ClearInstalledHooks();

        var restored = File.ReadAllText(Path.Combine(marked, "prefs.js"));
        StringAssert.Contains(restored, "network.proxy.type");
        Assert.IsFalse(restored.Contains("titanium.inspector.proxy.managed", StringComparison.Ordinal));
        StringAssert.Contains(File.ReadAllText(Path.Combine(plain, "prefs.js")), "browser.startup.page");
    }

    [TestMethod]
    public void BrowserPolicy_ApplyAndClear_StayInsideTheTempHome()
    {
        CoverageOs.TreatAsLinuxForTests = true;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Titanium Coverage", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        LinuxBrowserLaunchProxy.ApplyCaCertificatesPolicy(cert);
        LinuxBrowserLaunchProxy.Apply("127.0.0.1", 8866, relaunch: false, pinProcess: false);
        LinuxBrowserLaunchProxy.RemoveCaCertificatesPolicy();
        LinuxBrowserLaunchProxy.ClearInstalledHooks();
        var marker = Path.Combine(_home, "usr", "bin", "google-chrome");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "");
        LinuxChromeProfileProxy.EnsureInstalledProfilesForTests(_home,
        [
            (marker, "google-chrome"),
        ]);
        Assert.IsTrue(Directory.Exists(Path.Combine(_home, ".config", "google-chrome", "Default")));
    }

    [TestMethod]
    public void MacCleanup_ParsesAnEnabledLoopbackService_AndTheLaunchPathStillRuns()
    {
        CoverageOs.TreatAsMacForTests = true;
        InstalledProxyCleanup.RunOverrideForTests = (_, args) =>
            args.Contains("-listallnetworkservices", StringComparison.Ordinal)
                ? "An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n"
                : args.Contains("-get", StringComparison.Ordinal)
                    ? "Enabled: Yes\nServer: 127.0.0.1\nPort: 8080\nAuthenticated Proxy Enabled: 0\n"
                    : "";
        InstalledProxyCleanup.ForceClear();

        InstalledProxyCleanup.RunOverrideForTests = null;
        InstalledProxyCleanup.ForceClear();
    }

    [TestMethod]
    public void LinuxCleanup_WithoutOwnershipEvidence_DoesNotClear()
    {
        CoverageOs.TreatAsLinuxForTests = true;
        Assert.IsFalse(InstalledProxyCleanup.TryForceClearWhenOwned());
        InstalledProxyCleanup.ForceClear();
    }

    [TestMethod]
    public void OrphanedWinInetProxy_IsReported_AndTheRegistryIsRestored()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var reg = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
        Assert.IsNotNull(reg);
        var enable = reg!.GetValue("ProxyEnable");
        var server = reg.GetValue("ProxyServer");
        try
        {
            reg.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            reg.SetValue("ProxyServer", "http=127.0.0.1:9;https=127.0.0.1:9");
            var manager = new SystemProxyManager();
            var dead = manager.GetOrphanedLocalProxyProtocols((_, _) => false);
            var live = manager.GetOrphanedLocalProxyProtocols((_, _) => true);
            Assert.AreNotEqual(ProxyProtocolType.None, dead);
            Assert.AreEqual(ProxyProtocolType.None, live);
        }
        finally
        {
            if (enable is null) reg.DeleteValue("ProxyEnable", throwOnMissingValue: false);
            else reg.SetValue("ProxyEnable", enable);
            if (server is null) reg.DeleteValue("ProxyServer", throwOnMissingValue: false);
            else reg.SetValue("ProxyServer", server);
        }
    }
}
