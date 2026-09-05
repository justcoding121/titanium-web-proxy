using System.Diagnostics;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class FirefoxScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log, TimeSpan timeout)
    {
        var firefox = BrowserPaths.FindFirefox();
        if (firefox is null)
        {
            log.Step("firefox", false, "Firefox not installed");
            return 1;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        harness.Dialogs.InstallRootCaResult = true;
        harness.Dialogs.InstallRootCaBeforeFirefoxResult = true;
        harness.Dialogs.QuitFirefoxForTrustResult = true;

        await harness.OnUiAsync(() => harness.Robot.Click("MenuInstallCa")).ConfigureAwait(true);
        log.Info("Waiting for OS CA trust if prompted…");
        var trustDeadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < trustDeadline &&
               !harness.Interception.IsRootTrusted &&
               !harness.Interception.VerifyOsUserSslTrust())
            await Task.Delay(400).ConfigureAwait(true);

        if (!harness.Interception.IsRootTrusted && !harness.Interception.VerifyOsUserSslTrust())
        {
            log.Step("firefox-ca", false, "Root CA not trusted");
            return 1;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuTrustFirefoxCa")).ConfigureAwait(true);
        await Task.Delay(1500).ConfigureAwait(true);
        log.Step("firefox-trust-menu", true, harness.ViewModel.StatusText ?? "");

        await harness.OnUiAsync(() => harness.Robot.SetCheck("DecryptHttpsCheck", true)).ConfigureAwait(true);
        await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", true)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        foreach (var p in Process.GetProcessesByName("firefox"))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        await Task.Delay(1000).ConfigureAwait(true);

        var host = SystemProxyBrowserCapture.DefaultProbeHost;
        Process? proc = null;
        SessionSnapshot? captured = null;
        void OnSession(object? _, SessionSnapshot s)
        {
            if (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase))
                captured = s;
        }

        harness.Interception.SessionCaptured += OnSession;
        try
        {
            proc = SystemProxyBrowserCapture.StartFirefox(firefox, $"https://{host}/");
            var deadline = DateTime.UtcNow + timeout;
            while (captured is null && DateTime.UtcNow < deadline)
                await Task.Delay(250).ConfigureAwait(true);

            if (captured is null)
            {
                log.Step("firefox-capture", false, "No session via system proxy");
                return 1;
            }

            log.Step("firefox-capture", true, captured.Url);
            return 0;
        }
        finally
        {
            harness.Interception.SessionCaptured -= OnSession;
            SystemProxyBrowserCapture.TryKill(proc);
            await harness.OnUiAsync(() =>
            {
                if (harness.ViewModel.SystemProxy)
                    harness.Robot.SetCheck("SystemProxyCheck", false);
            }).ConfigureAwait(true);
        }
    }
}
