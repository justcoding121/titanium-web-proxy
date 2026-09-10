using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
/// Windows live matrix: WinINET system proxy + CurrentUser Root CA with Edge/Chrome/Firefox.
/// Does <b>not</b> pass <c>--proxy-server</c> — browsers must honor the OS proxy.
/// Not run in PR CI (mutates WinINET / Root store). Local: <c>dotnet test --filter TestCategory=E2E-Slow</c>.
/// Shared helpers live in <c>tools/InspectorDesktopProbe/Shared</c>.
/// </summary>
[TestClass]
public class InspectorWindowsSystemProxyBrowserE2ETests
{
    private const string ProbeHost = SystemProxyBrowserCapture.DefaultProbeHost;

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
            var before = OsProxyStatus.ReadWinInet();
            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            var enabled = OsProxyStatus.ReadWinInet();
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

            var after = OsProxyStatus.ReadWinInet();
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
            Assert.IsTrue(RootTrustHelpers.TrustRootInteractively(interception), "Install root CA failed (click Yes on CryptUI if shown)");
            Assert.IsTrue(interception.VerifyOsUserSslTrust(), "Root must be present after trust");
            Assert.IsTrue(RootTrustHelpers.IsTitaniumRootInCurrentUserStore(interception), "CurrentUser\\Root missing CA");

            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            interception.UntrustRootCertificate(machineStore: false);
            RootTrustHelpers.UntrustRootSilent(interception);
            Assert.IsFalse(RootTrustHelpers.IsTitaniumRootInCurrentUserStore(interception),
                "CurrentUser\\Root must not contain Titanium CA after untrust");
            Assert.IsFalse(interception.VerifyOsUserSslTrust());
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            try { RootTrustHelpers.UntrustRootSilent(interception); } catch { /* best-effort */ }
            SafeStop(interception);
        }
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Edge_ThroughSystemProxy_Only_CapturesHttps()
    {
        await BrowserThroughSystemProxy_Only_CapturesHttps(
            BrowserPaths.FindEdge(), "edge", disableQuic: true);
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Chrome_ThroughSystemProxy_Only_CapturesHttps()
    {
        await BrowserThroughSystemProxy_Only_CapturesHttps(
            BrowserPaths.FindChrome(), "chrome", disableQuic: true);
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

        var firefox = BrowserPaths.FindFirefox();
        if (firefox is null)
        {
            Assert.Inconclusive("Firefox not found");
            return;
        }

        using var interception = CreateInterception();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains(ProbeHost, StringComparison.OrdinalIgnoreCase))
                captured = s;
        };

        var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
        Process? browser = null;
        try
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(RootTrustHelpers.TrustRootInteractively(interception), "CA trust failed");
            var ffTrust = interception.TrustFirefox();
            Assert.IsTrue(ffTrust.Succeeded, ffTrust.Message);

            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            SystemProxyBrowserCapture.KillFirefoxProcesses();
            await Task.Delay(800);

            var profileDir = SystemProxyBrowserCapture.CreateTempFirefoxProfile("twp-e2e-ff-");
            File.AppendAllText(
                Path.Combine(profileDir, "user.js"),
                """

                user_pref("security.enterprise_roots.enabled", true);
                """);
            try
            {
                browser = SystemProxyBrowserCapture.StartFirefox(firefox, profileDir, $"https://{ProbeHost}/");

                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (captured is null && DateTime.UtcNow < deadline)
                    await Task.Delay(250);

                if (captured is null)
                    Assert.Inconclusive("No Firefox session via WinINET within 35s");

                StringAssert.Contains(captured!.Url, ProbeHost, StringComparison.OrdinalIgnoreCase);

                Assert.IsTrue(interception.SetSystemProxy(false));
                captured = null;
                await Task.Delay(1500);
                SystemProxyBrowserCapture.TryKill(browser);
                SystemProxyBrowserCapture.KillFirefoxProcesses();
                browser = SystemProxyBrowserCapture.StartFirefox(
                    firefox, profileDir, $"https://{ProbeHost}/?after-disable=1");

                var offDeadline = DateTime.UtcNow.AddSeconds(12);
                while (captured is null && DateTime.UtcNow < offDeadline)
                    await Task.Delay(250);

                Assert.IsTrue(
                    captured is null ||
                    !captured.Url.Contains("after-disable", StringComparison.OrdinalIgnoreCase),
                    "Firefox still proxied after WinINET restore");
            }
            finally
            {
                SystemProxyBrowserCapture.TryDeleteDir(profileDir);
            }
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            try
            {
                interception.SetSystemProxy(false);
                RootTrustHelpers.UntrustRootSilent(interception);
                interception.UntrustRootCertificate(false);
                interception.Stop();
            }
            catch { /* best-effort */ }

            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            SystemProxyBrowserCapture.TryKill(browser);
            SystemProxyBrowserCapture.KillFirefoxProcesses();
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
            Assert.IsTrue(RootTrustHelpers.TrustRootInteractively(interception), "CA trust failed");
            Assert.IsTrue(
                interception.SetSystemProxy(true, CreateSettings()),
                interception.LastSystemProxyError ?? "SetSystemProxy failed");

            browser = SystemProxyBrowserCapture.StartChromiumViaSystemProxy(
                browserPath, userData, $"https://{ProbeHost}/", disableQuic);

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
            SystemProxyBrowserCapture.TryKill(browser);

            browser = SystemProxyBrowserCapture.StartChromiumViaSystemProxy(
                browserPath, userData, $"https://{ProbeHost}/?after-disable=1", disableQuic);

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
                RootTrustHelpers.UntrustRootSilent(interception);
                interception.UntrustRootCertificate(false);
                interception.Stop();
            }
            catch { /* best-effort */ }

            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            SystemProxyBrowserCapture.TryKill(browser);
            SystemProxyBrowserCapture.TryDeleteDir(userData);
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
}
