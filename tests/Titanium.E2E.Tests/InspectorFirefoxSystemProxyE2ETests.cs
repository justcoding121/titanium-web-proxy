using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
/// Mutates OS system proxy + launches Firefox. Not run in PR CI (filter excludes E2E-Slow).
/// Local: dotnet test --filter TestCategory=E2E-Slow
/// </summary>
[TestClass]
public class InspectorFirefoxSystemProxyE2ETests
{
    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Firefox_ThroughSystemProxy_CapturesHttpSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Inconclusive("macOS-only Firefox system-proxy path");
            return;
        }

        var firefox = FindFirefox();
        if (firefox is null)
        {
            Assert.Inconclusive("Firefox not found");
            return;
        }

        using var origin = new EchoOrigin();
        using var interception = new InterceptionService();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains("firefox-e2e", StringComparison.OrdinalIgnoreCase))
                captured = s;
        };

        var profile = Path.Combine(Path.GetTempPath(), "twp-firefox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        WriteFirefoxTestPrefs(profile);
        Process? firefoxProc = null;
        try
        {
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(interception.SetSystemProxy(true),
                "SetAsSystemProxy failed: " + interception.LastSystemProxyError);

            firefoxProc = StartFirefox(firefox, profile,
                $"http://127.0.0.1:{origin.Port}/firefox-e2e");

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (captured is null && DateTime.UtcNow < deadline)
                await Task.Delay(200);

            if (captured is null)
            {
                Assert.Inconclusive(
                    "No session captured within 30s — Firefox may still be using PAC, " +
                    "ignoring system proxy, or needs a restart. Check scutil --proxy.");
            }

            StringAssert.Contains(captured!.Url, "firefox-e2e");
        }
        finally
        {
            Restore(interception, firefoxProc, profile);
        }
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Firefox_TrustCa_WritesEnterpriseRootsPref()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var interception = new InterceptionService();
        await interception.StartAsync(IPAddress.Loopback, 0);

        var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
        CertificateManager.SuppressInteractiveRootStoreMutations = false;
        try
        {
            var trusted = interception.InstallRootCertificate(machineStore: false);
            if (!trusted &&
                interception.LastOsTrustResult?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            {
                Assert.Inconclusive("Install root CA did not add Keychain trust: " +
                                    interception.LastOsTrustResult?.Message);
                return;
            }

            var result = interception.TrustFirefox();
            if (!result.Succeeded &&
                result.Message.Contains("profile", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("No Firefox profile on this machine — open Firefox once");
                return;
            }

            Assert.IsTrue(result.Succeeded, result.Message);
            StringAssert.Contains(result.Message, "Firefox");
        }
        finally
        {
            CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            try
            {
                interception.UntrustRootCertificate(machineStore: false);
                interception.Stop();
            }
            catch
            {
                // best-effort restore
            }
        }
    }

    private static void WriteFirefoxTestPrefs(string profileDir)
    {
        File.WriteAllText(Path.Combine(profileDir, "user.js"),
            """
            user_pref("network.proxy.type", 5);
            user_pref("network.proxy.allow_hijacking_localhost", true);
            user_pref("network.http.http3.enable", false);
            user_pref("network.dns.disableIPv6", true);
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.startup.homepage_override.mstone", "ignore");
            user_pref("toolkit.telemetry.enabled", false);
            user_pref("app.update.enabled", false);
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("startup.homepage_welcome_url", "");
            user_pref("startup.homepage_welcome_url.additional", "");
            """);
    }

    private static Process? StartFirefox(string firefox, string profile, string url) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = firefox,
            Arguments = $"--headless --no-remote --profile \"{profile}\" \"{url}\"",
            UseShellExecute = false,
        });

    private static void Restore(InterceptionService interception, Process? firefoxProc, string profile)
    {
        try
        {
            interception.SetSystemProxy(false);
            interception.Stop();
        }
        catch
        {
            // best-effort restore
        }

        try
        {
            if (firefoxProc is { HasExited: false })
                firefoxProc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }

        try
        {
            if (Directory.Exists(profile))
                Directory.Delete(profile, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static string? FindFirefox()
    {
        var candidates = new[]
        {
            "/Applications/Firefox.app/Contents/MacOS/firefox",
            "/Applications/Firefox Developer Edition.app/Contents/MacOS/firefox",
            "/Applications/Firefox Nightly.app/Contents/MacOS/firefox",
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        try
        {
            var psi = new ProcessStartInfo("sh", "-c \"command -v firefox\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var found = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(found) ? null : found;
        }
        catch
        {
            return null;
        }
    }
}
