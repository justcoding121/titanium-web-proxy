using System.Diagnostics;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class CertScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log, string browser, TimeSpan timeout)
    {
        log.Info("Install CA may show OS Trusted Root Yes/No (Windows) or Keychain password (macOS) — click Yes once.");

        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        harness.Dialogs.InstallRootCaResult = true;
        await harness.OnUiAsync(() => harness.Robot.Click("MenuInstallCa")).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);

        var trusted = harness.Interception.IsRootTrusted || harness.Interception.VerifyOsUserSslTrust()
                      || (OperatingSystem.IsWindows() && RootTrustHelpers.IsTitaniumRootInCurrentUserStore(harness.Interception));
        if (!trusted)
        {
            // Allow time for human CryptUI click
            log.Warn("Waiting up to 60s for OS trust dialog confirmation…");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!trusted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500).ConfigureAwait(true);
                trusted = harness.Interception.IsRootTrusted || harness.Interception.VerifyOsUserSslTrust()
                          || (OperatingSystem.IsWindows() && RootTrustHelpers.IsTitaniumRootInCurrentUserStore(harness.Interception));
            }
        }

        if (!trusted)
        {
            log.Step("install-ca", false, "Root CA not trusted (dismissed OS dialog?)");
            return 1;
        }

        log.Step("install-ca", true, "trusted");

        await harness.OnUiAsync(() => harness.Robot.SetCheck("DecryptHttpsCheck", true)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.ViewModel.DecryptHttps, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        log.Step("decrypt-on", true, "DecryptHttps=true");

        await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", true)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var path = ChromiumPathForDecrypt(browser);
        if (path is not null)
        {
            var host = SystemProxyBrowserCapture.DefaultProbeHost;
            var userData = Path.Combine(Path.GetTempPath(), "twp-probe-cert-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userData);
            Process? proc = null;
            SessionSnapshot? captured = null;
            void OnSession(object? _, SessionSnapshot s)
            {
                if (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase) && !s.IsTunnel)
                    captured = s;
            }

            harness.Interception.SessionCaptured += OnSession;
            try
            {
                proc = SystemProxyBrowserCapture.StartChromiumViaSystemProxy(
                    path, userData, $"https://{host}/", disableQuic: true);
                var deadline = DateTime.UtcNow + timeout;
                while (captured is null && DateTime.UtcNow < deadline)
                    await Task.Delay(250).ConfigureAwait(true);

                if (captured is null)
                    log.Step("decrypt-capture", false, "No decrypted HTTPS session (CONNECT-only or timeout)");
                else
                    log.Step("decrypt-capture", true, captured.Url);
            }
            finally
            {
                harness.Interception.SessionCaptured -= OnSession;
                SystemProxyBrowserCapture.TryKill(proc);
                SystemProxyBrowserCapture.TryDeleteDir(userData);
            }
        }
        else
        {
            log.Warn("No Chromium browser for decrypt capture check");
        }

        harness.Dialogs.RemoveRootCaResult = true;
        await harness.OnUiAsync(() => harness.Robot.Click("MenuRemoveCa")).ConfigureAwait(true);
        await Task.Delay(800).ConfigureAwait(true);

        // Product turns Decrypt HTTPS off after uninstall.
        var decryptOff = !harness.ViewModel.DecryptHttps;
        var checkOff = harness.Robot.GetCheck("DecryptHttpsCheck") != true;
        if (!decryptOff)
        {
            log.Step("decrypt-auto-off", false, "DecryptHttps still true after Remove CA");
            return 1;
        }

        log.Step("decrypt-auto-off", true, checkOff ? "DecryptHttpsCheck off" : "VM DecryptHttps=false");

        if (OperatingSystem.IsWindows())
            RootTrustHelpers.UntrustRootSilent(harness.Interception);

        await harness.OnUiAsync(() =>
        {
            if (harness.ViewModel.SystemProxy)
                harness.Robot.SetCheck("SystemProxyCheck", false);
        }).ConfigureAwait(true);

        return 0;
    }

    private static string? ChromiumPathForDecrypt(string browser)
    {
        if (!browser.Equals("safari", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = BrowserPaths.Resolve(browser);
            if (resolved is not null &&
                !resolved.Contains("Safari.app", StringComparison.Ordinal))
                return resolved;
        }

        return BrowserPaths.FindEdge() ?? BrowserPaths.FindChrome();
    }
}
