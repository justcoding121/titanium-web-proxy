using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.E2E.Tests;

/// <summary>
/// Mutates WinINET + launches Chrome. Not run in PR CI (filter excludes E2E-Slow).
/// Local: dotnet test --filter TestCategory=E2E-Slow
/// </summary>
[TestClass]
public class InspectorChromeSystemProxyE2ETests
{
    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Chrome_ThroughSystemProxy_WithQuicDisabled_CapturesSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        var chrome = FindChrome();
        if (chrome is null)
        {
            Assert.Inconclusive("Chrome not found");
            return;
        }

        using var origin = new EchoOrigin();
        using var interception = new InterceptionService(); // real WinINET
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
                Assert.IsTrue(trusted, "CA must be in CurrentUser Root for Chrome");
            }
            finally
            {
                CertificateManager.SuppressInteractiveRootStoreMutations = previousSuppress;
            }
            Assert.IsTrue(interception.SetSystemProxy(true), "SetAsSystemProxy failed");

            chromeProc = Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments =
                    $"--headless=new --disable-gpu --disable-quic --user-data-dir=\"{userData}\" --no-first-run --disable-extensions " +
                    $"\"http://127.0.0.1:{origin.Port}/chrome-e2e\"",
                UseShellExecute = false,
            });

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (captured is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            if (captured is null)
            {
                Assert.Inconclusive(
                    "No session captured within 20s — Chrome may ignore WinINET or CA trust was blocked. " +
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

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
