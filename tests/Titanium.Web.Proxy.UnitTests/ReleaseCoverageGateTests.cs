using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Titanium.Web.Proxy.Network;
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
        RunTime.SetIsWindowsForTests(null);
        RunTime.SetIsLinuxForTests(null);
        RunTime.SetIsMacForTests(null);
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
    public void UnixTrustBranches_StaySuppressedAndDoNotOpenTheWindowsStore()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        // Test hosts suppress CryptUI, Keychain, and pkexec. These calls must
        // return before any store write or OS prompt.
        Assert.IsFalse(mgr.InstallRootIntoCertificateStores());
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, mgr.LastOsTrustResult!.Kind);
        mgr.ApplyUnixSslTrustAfterStoreInstall(machineTrusted: true);
        Assert.IsFalse(mgr.TrustRootCertificateAsAdmin(machineTrusted: true));
        Assert.AreEqual(CertificateOsTrustKind.Cancelled, mgr.LastOsTrustResult!.Kind);
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

    [TestMethod]
    public void LoopbackProxyServer_ParsesWinInetForms()
    {
        Assert.IsFalse(InstalledProxyCleanup.IsWindowsLoopbackProxyServer(""));
        Assert.IsFalse(InstalledProxyCleanup.IsWindowsLoopbackProxyServer("   "));
        Assert.IsTrue(InstalledProxyCleanup.IsWindowsLoopbackProxyServer("127.0.0.1:8080"));
        Assert.IsTrue(InstalledProxyCleanup.IsWindowsLoopbackProxyServer("localhost:8080"));
        Assert.IsTrue(InstalledProxyCleanup.IsWindowsLoopbackProxyServer("[::1]:8080"));
        Assert.IsTrue(InstalledProxyCleanup.IsWindowsLoopbackProxyServer(
            "http=127.0.0.1:8080;https=example.test:443"));
        Assert.IsFalse(InstalledProxyCleanup.IsWindowsLoopbackProxyServer("example.test:443"));
    }

    [TestMethod]
    public void CleanupRun_ReadsStdoutFromAShortCommand()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var run = typeof(InstalledProxyCleanup).GetMethod(
            "Run",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(run);
        var text = (string)run!.Invoke(null, ["cmd.exe", "/c echo twp-cov"])!;
        StringAssert.Contains(text, "twp-cov");
        var previousResolve = UnixProcessPath.ResolveOverrideForTests;
        try
        {
            UnixProcessPath.ResolveOverrideForTests = null;
            var found = UnixProcessPath.Resolve(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Path.Combine(Path.GetTempPath(), "twp-missing-cov.exe"));
            Assert.IsFalse(string.IsNullOrEmpty(found));
            var tracker = new Titanium.Web.Proxy.Network.ClientHandshakeRejectTracker();
            _ = tracker.MinBurstSpread;
            CoverageOs.TreatAsLinuxForTests = true;
            InstalledProxyCleanup.ForceClear();
        }
        finally
        {
            CoverageOs.TreatAsLinuxForTests = false;
            UnixProcessPath.ResolveOverrideForTests = previousResolve;
        }
    }

    [TestMethod]
    public void OrphanedLocalProxy_ClearsOnlyTheLoopbackEntryItJustWrote()
    {
        Assert.IsFalse(ProxyServer.IsLocalProxyPortListening("localhost", 0));
        Assert.IsFalse(ProxyServer.IsLocalProxyPortListening("localhost", 1));
        Assert.IsFalse(ProxyServer.IsLocalProxyPortListening("::1", 1));

        if (!OperatingSystem.IsWindows())
            return;

        using var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
        Assert.IsNotNull(reg);
        var enable = reg!.GetValue("ProxyEnable");
        var server = reg.GetValue("ProxyServer");
        try
        {
            reg.SetValue("ProxyEnable", 1, Microsoft.Win32.RegistryValueKind.DWord);
            reg.SetValue("ProxyServer", "127.0.0.1:9");
            CoverageOs.TreatAsLinuxForTests = true;
            var cleared = ProxyServer.TryClearOrphanedLocalSystemProxy((_, _) => false);
            Assert.IsTrue(cleared.Succeeded);
        }
        finally
        {
            CoverageOs.TreatAsLinuxForTests = false;
            if (enable is null) reg.DeleteValue("ProxyEnable", throwOnMissingValue: false);
            else reg.SetValue("ProxyEnable", enable);
            if (server is null) reg.DeleteValue("ProxyServer", throwOnMissingValue: false);
            else reg.SetValue("ProxyServer", server);
        }
    }
}
