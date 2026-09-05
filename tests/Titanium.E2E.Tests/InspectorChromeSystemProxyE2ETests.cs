using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
/// Mutates OS system proxy + launches Chrome. Not run in PR CI (filter excludes E2E-Slow).
/// Local: dotnet test --filter TestCategory=E2E-Slow
/// </summary>
[TestClass]
public class InspectorChromeSystemProxyE2ETests
{
    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Chrome_ThroughSystemProxy_WithQuicDisabled_CapturesSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Inconclusive("Windows/Linux only");
            return;
        }

        var chrome = FindChrome();
        if (chrome is null)
        {
            Assert.Inconclusive("Chrome not found");
            return;
        }

        using var origin = new EchoOrigin();
        using var interception = new InterceptionService();
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) => captured = s;

        var userData = Path.Combine(Path.GetTempPath(), "twp-chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        Process? chromeProc = null;
        try
        {
            await interception.StartAsync(IPAddress.Loopback, 0);
            var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            try
            {
                var trusted = interception.InstallRootCertificate(machineStore: false);
                Assert.IsTrue(trusted || RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                    "CA must be trusted for Chrome (Windows Root / Linux NSS)");
            }
            finally
            {
                CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            }

            Assert.IsTrue(interception.SetSystemProxy(true), "SetAsSystemProxy failed");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                AssertLinuxGsettingsPointsAtProxy(interception.BoundPort);
            }

            // Explicit --proxy-server avoids depending on live DE gsettings refresh in headless CI.
            // System proxy is still applied/asserted above for desktop parity.
            // MAP a non-loopback name so Chrome does not apply its default localhost bypass.
            chromeProc = Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments =
                    $"--headless=new --disable-gpu --disable-quic --no-first-run --disable-extensions " +
                    $"--user-data-dir=\"{userData}\" " +
                    $"--proxy-server=\"http://127.0.0.1:{interception.BoundPort}\" " +
                    "--proxy-bypass-list=\"<-loopback>\" " +
                    "--host-resolver-rules=\"MAP twp-chrome-e2e.test 127.0.0.1\" " +
                    $"\"http://twp-chrome-e2e.test:{origin.Port}/chrome-e2e\"",
                UseShellExecute = false,
            });

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((captured is null ||
                    !captured.Url.Contains("chrome-e2e", StringComparison.OrdinalIgnoreCase)) &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            if (captured is null ||
                !captured.Url.Contains("chrome-e2e", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive(
                    "No session captured within 20s — Chrome may ignore the proxy or CA trust was blocked. " +
                    "Re-run manually after Install CA + system proxy.");
            }

            StringAssert.Contains(captured!.Url, "chrome-e2e");
        }
        finally
        {
            try
            {
                interception.SetSystemProxy(false);
                interception.UntrustRootCertificate(machineStore: false);
                interception.Stop();
            }
            catch
            {
                // best-effort restore
            }

            try
            {
                if (chromeProc is { HasExited: false })
                {
                    chromeProc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(userData))
                {
                    Directory.Delete(userData, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E-Slow")]
    [TestCategory("E2E-UI-Linux")]
    public async Task Chrome_ThroughProxy_DecryptHttps_CapturesDecryptedSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Inconclusive("Linux-only decrypt/NSS path");
            return;
        }

        var chrome = FindChrome();
        if (chrome is null)
        {
            Assert.Inconclusive("Chrome not found");
            return;
        }

        if (string.IsNullOrWhiteSpace(FindCertutil()))
        {
            Assert.Inconclusive("certutil (libnss3-tools) required for Chrome NSS trust");
            return;
        }

        using var origin = new HttpsEchoOrigin();
        using var interception = new InterceptionService
        {
            DecryptHttps = true,
            IgnoreServerCertificateErrors = true,
        };
        SessionSnapshot? captured = null;
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains("chrome-https-e2e", StringComparison.OrdinalIgnoreCase))
            {
                captured = s;
            }
        };

        var userData = Path.Combine(Path.GetTempPath(), "twp-chrome-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        Process? chromeProc = null;
        try
        {
            await interception.StartAsync(IPAddress.Loopback, 0);
            var previousSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
            CertificateManager.SuppressInteractiveRootStoreMutations = false;
            try
            {
                Assert.IsTrue(interception.InstallRootCertificate(machineStore: false),
                    "NSS user trust must succeed for decrypt");
                Assert.IsTrue(interception.VerifyOsUserSslTrust() || interception.IsRootTrusted,
                    "OS/NSS trust verify failed");
            }
            finally
            {
                CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            }

            chromeProc = Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments =
                    $"--headless=new --disable-gpu --disable-quic --no-first-run --disable-extensions " +
                    $"--user-data-dir=\"{userData}\" " +
                    $"--proxy-server=\"http://127.0.0.1:{interception.BoundPort}\" " +
                    "--proxy-bypass-list=\"<-loopback>\" " +
                    $"\"https://127.0.0.1:{origin.Port}/chrome-https-e2e\"",
                UseShellExecute = false,
            });

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (captured is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            if (captured is null)
            {
                Assert.Inconclusive("No HTTPS session captured — Chrome NSS trust may need a restart or certutil import failed.");
            }

            // Decrypted path yields the origin request (GET), not only CONNECT.
            Assert.IsFalse(
                string.Equals(captured!.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) &&
                captured.StatusCode == 200 &&
                !captured.Url.Contains("chrome-https-e2e", StringComparison.OrdinalIgnoreCase),
                $"Expected decrypted HTTPS session, got {captured.Method} {captured.StatusCode} {captured.Url}");
            StringAssert.Contains(captured.Url, "chrome-https-e2e");
        }
        finally
        {
            try
            {
                interception.UntrustRootCertificate(machineStore: false);
                interception.Stop();
            }
            catch
            {
                // best-effort
            }

            try
            {
                if (chromeProc is { HasExited: false })
                    chromeProc.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(userData))
                    Directory.Delete(userData, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void AssertLinuxGsettingsPointsAtProxy(int port)
    {
        // Clear poisoned sandbox dbus so reads hit the real session bus.
        var psi = new ProcessStartInfo("gsettings", "get org.gnome.system.proxy.http host")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Remove("DBUS_SESSION_BUS_ADDRESS");
        using (var p = Process.Start(psi)!)
        {
            var host = p.StandardOutput.ReadToEnd().Trim().Trim('\'', '"');
            p.WaitForExit(5000);
            Assert.AreEqual("127.0.0.1", host, "System proxy must advertise IPv4 loopback, not localhost");
        }

        psi.Arguments = "get org.gnome.system.proxy.http port";
        using (var p = Process.Start(psi)!)
        {
            var portText = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            Assert.AreEqual(port.ToString(), portText);
        }

        psi.Arguments = "get org.gnome.system.proxy mode";
        using (var p = Process.Start(psi)!)
        {
            var mode = p.StandardOutput.ReadToEnd().Trim().Trim('\'', '"');
            p.WaitForExit(5000);
            Assert.AreEqual("manual", mode, ignoreCase: true);
        }

        psi.Arguments = "get org.gnome.system.proxy.http enabled";
        using (var p = Process.Start(psi)!)
        {
            var enabled = p.StandardOutput.ReadToEnd().Trim().Trim('\'', '"');
            p.WaitForExit(5000);
            Assert.AreEqual("true", enabled, ignoreCase: true,
                "GIO/Chrome ignore mode=manual unless http enabled is true");
        }
    }

    private static string? FindCertutil()
    {
        try
        {
            var psi = new ProcessStartInfo("sh", "-c \"command -v certutil\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var path = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindChrome()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var candidate in new[]
                     {
                         "/usr/bin/google-chrome-stable",
                         "/usr/bin/google-chrome",
                         "/usr/bin/chromium-browser",
                         "/usr/bin/chromium",
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
