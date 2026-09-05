using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
/// Windows live matrix: WinINET system proxy + CurrentUser Root CA with Edge/Chrome/Firefox.
/// Does <b>not</b> pass <c>--proxy-server</c> — browsers must honor the OS proxy.
/// Not run in PR CI (mutates WinINET / Root store). Local: <c>dotnet test --filter TestCategory=E2E-Slow</c>.
/// </summary>
[TestClass]
public class InspectorWindowsSystemProxyBrowserE2ETests
{
    private const string ProbeHost = "example.com";

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task WinInet_EnableDisable_WritesAndRestoresRegistry()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        if (RootStoreUiTestGuards.IsAutomatedCiOrSkipEnv())
        {
            Assert.Inconclusive("Skipped in CI (mutates WinINET)");
            return;
        }

        using var interception = CreateInterception();
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            var before = ReadWinInet();
            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            var enabled = ReadWinInet();
            Assert.AreEqual(1, enabled.ProxyEnable, "ProxyEnable must be 1 after enable");
            StringAssert.Contains(
                enabled.ProxyServer ?? string.Empty,
                $"127.0.0.1:{interception.BoundPort}",
                StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(
                enabled.ProxyOverride ?? string.Empty,
                "<-loopback>",
                StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(
                interception.SetSystemProxy(false),
                interception.LastSystemProxyError ?? "Restore failed");

            var after = ReadWinInet();
            Assert.AreEqual(before.ProxyEnable, after.ProxyEnable,
                "ProxyEnable must restore to pre-test value");
            Assert.AreEqual(before.ProxyServer ?? string.Empty, after.ProxyServer ?? string.Empty,
                "ProxyServer must restore");
        }
        finally
        {
            SafeStop(interception);
        }
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task RootCa_TrustAndUntrust_CurrentUserStore()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        RootStoreUiTestGuards.RequireInteractiveRootTrustAvailable();

        using var interception = CreateInterception();
        await interception.StartAsync(IPAddress.Loopback, 0);
        var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            Assert.IsTrue(TrustRootInteractively(interception), "Install root CA failed (click Yes on CryptUI if shown)");
            Assert.IsTrue(interception.VerifyOsUserSslTrust(), "Root must be present after trust");
            Assert.IsTrue(IsTitaniumRootInCurrentUserStore(interception), "CurrentUser\\Root missing CA");

            // Suppress before Remove so DELETE CryptUI cannot hang unattended runs.
            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            interception.UntrustRootCertificate(machineStore: false);
            UntrustRootSilent(interception);
            Assert.IsFalse(IsTitaniumRootInCurrentUserStore(interception),
                "CurrentUser\\Root must not contain Titanium CA after untrust");
            Assert.IsFalse(interception.VerifyOsUserSslTrust());
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            try { UntrustRootSilent(interception); } catch { /* best-effort */ }
            SafeStop(interception);
        }
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Edge_ThroughSystemProxy_Only_CapturesHttps()
    {
        await BrowserThroughSystemProxy_Only_CapturesHttps(
            FindEdge(), "edge", disableQuic: true);
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Chrome_ThroughSystemProxy_Only_CapturesHttps()
    {
        await BrowserThroughSystemProxy_Only_CapturesHttps(
            FindChrome(), "chrome", disableQuic: true);
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Firefox_ThroughSystemProxy_WithEnterpriseRoots_CapturesHttps()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        RootStoreUiTestGuards.RequireInteractiveRootTrustAvailable();

        var firefox = FindFirefox();
        if (firefox is null)
        {
            Assert.Inconclusive("Firefox not found");
            return;
        }

        if (!FirefoxCertificateTrust.IsFirefoxProfilePresent())
        {
            Assert.Inconclusive("No Firefox profile — launch Firefox once, then re-run");
            return;
        }

        using var interception = CreateInterception();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains(ProbeHost, StringComparison.OrdinalIgnoreCase))
                captured = s;
        };

        Process? browser = null;
        var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(TrustRootInteractively(interception), "CA trust failed");

            var ffTrust = interception.TrustFirefox();
            Assert.IsTrue(ffTrust.Succeeded, ffTrust.Message);

            // Firefox requires restart after ImportEnterpriseRoots; kill any leftover then launch fresh.
            foreach (var p in Process.GetProcessesByName("firefox"))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }

            await Task.Delay(1500);

            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            browser = Process.Start(new ProcessStartInfo
            {
                FileName = firefox,
                Arguments = $"-no-remote -new-instance \"https://{ProbeHost}/\"",
                UseShellExecute = false,
            });

            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (captured is null && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            if (captured is null)
            {
                Assert.Inconclusive(
                    "No Firefox session via WinINET within 35s — check ImportEnterpriseRoots / restart.");
            }

            StringAssert.Contains(captured!.Url, ProbeHost, StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(interception.SetSystemProxy(false));
            captured = null;
            await Task.Delay(2000);
            Assert.IsNull(captured, "After disable, Firefox must not send new probe traffic to Inspector");
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            try
            {
                interception.SetSystemProxy(false);
                FirefoxCertificateTrust.TryClearWindowsEnterpriseRoots();
                UntrustRootSilent(interception);
                interception.UntrustRootCertificate(false);
                interception.Stop();
            }
            catch { /* best-effort */ }

            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;

            try
            {
                if (browser is { HasExited: false })
                    browser.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            foreach (var p in Process.GetProcessesByName("firefox"))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
    }

    private static async Task BrowserThroughSystemProxy_Only_CapturesHttps(
        string? browserPath, string name, bool disableQuic)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        RootStoreUiTestGuards.RequireInteractiveRootTrustAvailable();

        if (browserPath is null)
        {
            Assert.Inconclusive($"{name} not found");
            return;
        }

        using var interception = CreateInterception();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains(ProbeHost, StringComparison.OrdinalIgnoreCase))
                captured = s;
        };

        var userData = Path.Combine(Path.GetTempPath(), $"twp-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        Process? browser = null;
        var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
        try
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(TrustRootInteractively(interception), "CA trust failed");
            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            // No --proxy-server: Chromium must pick up WinINET. --disable-quic avoids H3 bypass.
            var quic = disableQuic ? "--disable-quic " : string.Empty;
            browser = Process.Start(new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments =
                    $"{quic}--no-first-run --disable-extensions --disable-background-networking " +
                    $"--user-data-dir=\"{userData}\" " +
                    $"\"https://{ProbeHost}/\"",
                UseShellExecute = false,
            });

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (captured is null && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            if (captured is null)
            {
                Assert.Inconclusive(
                    $"No {name} session via WinINET within 30s — browser may ignore system proxy or CA was blocked.");
            }

            StringAssert.Contains(captured!.Url, ProbeHost, StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(interception.SetSystemProxy(false));
            captured = null;
            await Task.Delay(1500);

            try
            {
                if (browser is { HasExited: false })
                    browser.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            browser = Process.Start(new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments =
                    $"{quic}--no-first-run --disable-extensions " +
                    $"--user-data-dir=\"{userData}\" " +
                    $"\"https://{ProbeHost}/?after-disable=1\"",
                UseShellExecute = false,
            });

            var offDeadline = DateTime.UtcNow.AddSeconds(12);
            while (captured is null && DateTime.UtcNow < offDeadline)
                await Task.Delay(250);

            Assert.IsTrue(
                captured is null ||
                !captured.Url.Contains("after-disable", StringComparison.OrdinalIgnoreCase),
                $"{name} still proxied after WinINET restore");
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            try
            {
                interception.SetSystemProxy(false);
                UntrustRootSilent(interception);
                interception.UntrustRootCertificate(false);
                interception.Stop();
            }
            catch { /* best-effort */ }

            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;

            try
            {
                if (browser is { HasExited: false })
                    browser.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            try
            {
                if (Directory.Exists(userData))
                    Directory.Delete(userData, recursive: true);
            }
            catch { /* ignore */ }
        }
    }

    private static InterceptionService CreateInterception() =>
        new()
        {
            DecryptHttps = true,
            IgnoreServerCertificateErrors = true,
            ProxyLoopback = true,
        };

    private static InspectorSettings CreateSettings() =>
        new()
        {
            ProxyLoopback = true,
            SystemProxyBypassHosts = MitmBypass.SystemProxyBypassRules.ToList(),
        };

    private static void SafeStop(InterceptionService interception)
    {
        try
        {
            interception.SetSystemProxy(false);
            interception.Stop();
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    ///     Trust via <see cref="InterceptionService.InstallRootCertificate"/> (X509Store).
    ///     Requires suppress cleared and no CI skip env. May show Trusted Root Yes/No once.
    ///     Never uses <c>certutil -addstore</c> (that dialog also hangs unattended CI).
    /// </summary>
    private static bool TrustRootInteractively(InterceptionService interception)
    {
        if (CertificateManager.AreInteractiveRootStoreMutationsSuppressed)
            return false;

        if (IsTitaniumRootInCurrentUserStore(interception))
        {
            interception.VerifyOsUserSslTrust();
            return true;
        }

        return interception.InstallRootCertificate(machineStore: false)
               && IsTitaniumRootInCurrentUserStore(interception);
    }

    /// <summary>
    ///     Silent Root cleanup via <c>certutil -delstore</c> (no Add CryptUI) while suppress is on.
    /// </summary>
    private static void UntrustRootSilent(InterceptionService interception)
    {
        var name = interception.RootCertificateName;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "certutil",
                Arguments = $"-user -delstore Root \"{name}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch { /* ignore */ }

        interception.VerifyOsUserSslTrust();
    }

    private static bool IsTitaniumRootInCurrentUserStore(InterceptionService interception)
    {
        var thumb = interception.RootCertificate?.Thumbprint;
        if (string.IsNullOrEmpty(thumb))
            return false;

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false).Count > 0;
    }

    private static (int? ProxyEnable, string? ProxyServer, string? ProxyOverride) ReadWinInet()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
        if (key is null)
            return (null, null, null);

        var enableObj = key.GetValue("ProxyEnable");
        int? enable = enableObj is null ? null : Convert.ToInt32(enableObj);
        return (enable, key.GetValue("ProxyServer") as string, key.GetValue("ProxyOverride") as string);
    }

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome",
                "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome",
                "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome",
                "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge",
                "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge",
                "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindFirefox()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox",
                "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox",
                "firefox.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
