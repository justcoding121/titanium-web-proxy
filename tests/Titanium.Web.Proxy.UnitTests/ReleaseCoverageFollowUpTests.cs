using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Covers Linux helper branches the Windows coverage host can reach.
///     Every path stays inside a temp home and never starts sudo, a browser, or a trust prompt.
/// </summary>
[TestClass]
public class ReleaseCoverageFollowUpTests
{
    [TestCleanup]
    public void ResetFlags()
    {
        CoverageOs.TreatAsLinuxForTests = false;
        CoverageOs.TreatAsMacForTests = false;
        CoverageOs.HomeForTests = null;
        UnixProcessPath.ResolveOverrideForTests = null;
        InstalledProxyCleanup.RunOverrideForTests = null;
        LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
    }

    [TestMethod]
    public void BlankHome_ReturnsBeforeTouchingProfiles()
    {
        var previousHome = CoverageOs.HomeForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        try
        {
            CoverageOs.TreatAsLinuxForTests = false;
            LinuxFirefoxProxy.Clear();

            CoverageOs.TreatAsLinuxForTests = true;
            CoverageOs.HomeForTests = " ";
            LinuxFirefoxProxy.Clear();
            LinuxProxyFailOpen.Stop();
            Assert.IsFalse(LinuxInspectorProxyOwnership.HasEvidence(" "));
        }
        finally
        {
            CoverageOs.HomeForTests = previousHome;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
        }
    }

    [TestMethod]
    public void ClosedLoopbackPort_IsNotAccepting()
    {
        Assert.IsFalse(ProxyServer.IsLoopbackPortAccepting(1));
    }

    [TestMethod]
    public void MacClear_SkipsAServiceThatIsNotLoopback()
    {
        var previousRun = InstalledProxyCleanup.RunOverrideForTests;
        var previousMac = CoverageOs.TreatAsMacForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        try
        {
            CoverageOs.TreatAsLinuxForTests = false;
            CoverageOs.TreatAsMacForTests = true;
            InstalledProxyCleanup.RunOverrideForTests = (_, args) =>
                args.Contains("-listall", StringComparison.Ordinal)
                    ? "Ethernet\n"
                    : "Enabled: No\r\nServer: 8.8.8.8\r\n";
            InstalledProxyCleanup.ForceClear();
        }
        finally
        {
            InstalledProxyCleanup.RunOverrideForTests = previousRun;
            CoverageOs.TreatAsMacForTests = previousMac;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
        }
    }

    [TestMethod]
    public void PolicySync_DisposedCertAndBlockedDirs_StayInTheTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-pol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var previousHome = CoverageOs.HomeForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        try
        {
            CoverageOs.HomeForTests = home;
            CoverageOs.TreatAsLinuxForTests = true;

            using (var cert = CreateCert())
            {
                cert.Dispose();
                LinuxBrowserLaunchProxy.ApplyCaCertificatesPolicy(cert);
            }

            LinuxBrowserLaunchProxy.SyncMachinePolicyFileForTests(
                "x.json",
                "{}",
                [("C:\\bad|path", Array.Empty<string>())]);

            var managed = Path.Combine(home, ".config", "google-chrome", "policies", "managed");
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            File.WriteAllText(managed, "not-a-directory");
            using (var cert = CreateCert())
                LinuxBrowserLaunchProxy.ApplyCaCertificatesPolicy(cert);

            var caDir = Path.Combine(home, ".config", "chromium", "policies", "managed", "titanium-inspector-ca.json");
            if (File.Exists(caDir))
                File.Delete(caDir);
            Directory.CreateDirectory(caDir);
            LinuxBrowserLaunchProxy.RemoveCaCertificatesPolicy();
        }
        finally
        {
            CoverageOs.HomeForTests = previousHome;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void LauncherGuess_RewritesChromiumAndOperaScripts()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-launch-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(home, ".local", "share", "titanium-inspector", "launchers");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "chromium.sh"), "# " + LinuxBrowserLaunchProxy.DesktopMarker + "\n");
        File.WriteAllText(Path.Combine(dir, "opera.sh"), "# " + LinuxBrowserLaunchProxy.DesktopMarker + "\n");
        var previousHome = CoverageOs.HomeForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        var previousResolve = UnixProcessPath.ResolveOverrideForTests;
        try
        {
            CoverageOs.HomeForTests = home;
            CoverageOs.TreatAsLinuxForTests = true;
            UnixProcessPath.ResolveOverrideForTests = _ => null;
            LinuxBrowserLaunchProxy.ClearInstalledHooks();
            StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "chromium.sh")), "/usr/bin/chromium");
            StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "opera.sh")), "/usr/bin/opera");
        }
        finally
        {
            CoverageOs.HomeForTests = previousHome;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
            UnixProcessPath.ResolveOverrideForTests = previousResolve;
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task ChromeClear_CoversTheWatcherAndTheRetry()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-chrome-" + Guid.NewGuid().ToString("N"));
        var prefs = Path.Combine(home, ".config", "google-chrome", "Default", "Preferences");
        Directory.CreateDirectory(Path.GetDirectoryName(prefs)!);
        File.WriteAllText(prefs, "{}");
        var previousHome = CoverageOs.HomeForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        var previousResolve = UnixProcessPath.ResolveOverrideForTests;
        try
        {
            CoverageOs.HomeForTests = home;
            CoverageOs.TreatAsLinuxForTests = true;
            UnixProcessPath.ResolveOverrideForTests = _ => null;

            LinuxChromeProfileProxy.Apply("127.0.0.1", 9);
            await Task.Delay(150);
            File.WriteAllText(prefs, """{"proxy":{"mode":"fixed_servers","server":"http://127.0.0.1:9"}}""");
            await Task.Delay(600);
            StringAssert.Contains(File.ReadAllText(prefs), "system");

            LinuxBrowserLaunchProxy.Clear();
            await Task.Delay(800);
            LinuxChromeProfileProxy.Clear(startFailOpen: false);
            LinuxProxyFailOpen.Stop();
        }
        finally
        {
            CoverageOs.HomeForTests = previousHome;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
            UnixProcessPath.ResolveOverrideForTests = previousResolve;
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void FailOpen_LockedScopeAndUnwritableScopePath_AreCaught()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-fo-scope-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(home, ".config", "TitaniumInspector");
        Directory.CreateDirectory(dir);
        var scope = Path.Combine(dir, "fail-open-proxy.scope");
        File.WriteAllText(scope, "ti-fail-open-9.scope");
        var previousHome = CoverageOs.HomeForTests;
        var previousResolve = UnixProcessPath.ResolveOverrideForTests;
        var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var previousDbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        try
        {
            CoverageOs.HomeForTests = home;
            var pidPath = Path.Combine(dir, "fail-open-proxy.pid");
            File.WriteAllText(pidPath, "1");
            LinuxProxyFailOpen.StopIfCoveringPort(9);
            File.WriteAllText(pidPath, "12345");
            using (new FileStream(pidPath, FileMode.Open, FileAccess.Read, FileShare.None))
                LinuxProxyFailOpen.StopIfCoveringPort(9);

            using (new FileStream(scope, FileMode.Open, FileAccess.Read, FileShare.None))
                LinuxProxyFailOpen.Stop();

            File.Delete(scope);
            Directory.CreateDirectory(scope);
            var exe = FakeOsExecutable.Ensure("twp-cov-exit0.exe");
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", home);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=" + Path.Combine(home, "bus"));
            UnixProcessPath.ResolveOverrideForTests = candidates =>
            {
                var joined = string.Join(" ", candidates);
                return joined.Contains("python", StringComparison.Ordinal) ||
                       joined.Contains("systemd-run", StringComparison.Ordinal)
                    ? exe
                    : null;
            };
            LinuxProxyFailOpen.Start("127.0.0.1", 9);
            LinuxProxyFailOpen.Stop();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousDbus);
            UnixProcessPath.ResolveOverrideForTests = previousResolve;
            CoverageOs.HomeForTests = previousHome;
            LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }

    private static X509Certificate2 CreateCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Titanium Coverage CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
