using System.Diagnostics;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class ProxyScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log, string browser, TimeSpan timeout)
    {
        var browsers = ResolveBrowsers(browser);
        if (browsers.Count == 0)
        {
            log.Step("proxy", false, $"No browser found for '{browser}'");
            return 1;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        log.Step("start-capture", true, $"port={harness.Interception.BoundPort}");

        await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", true)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var port = harness.Interception.BoundPort;
        if (OperatingSystem.IsWindows())
        {
            if (!OsProxyStatus.WinInetPointsAt(port))
            {
                log.Step("proxy-os", false, OsProxyStatus.Dump());
                return 1;
            }

            log.Step("proxy-os", true, OsProxyStatus.Dump());
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                OsProxyStatus.AssertLinuxGsettingsPointsAtProxy(port);
                log.Step("proxy-os", true, OsProxyStatus.Dump());
            }
            catch (Exception ex)
            {
                log.Step("proxy-os", false, ex.Message);
                return 1;
            }
        }
        else
        {
            log.Step("proxy-os", true, OsProxyStatus.Dump());
        }

        var failures = 0;
        foreach (var (name, path) in browsers)
        {
            if (!await CaptureOnceAsync(harness, log, name, path, timeout).ConfigureAwait(true))
                failures++;
        }

        await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", false)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => !harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        log.Step("proxy-off", true, OsProxyStatus.Dump());

        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> CaptureOnceAsync(
        InspectorHarness harness, ProbeLog log, string name, string path, TimeSpan timeout)
    {
        var host = SystemProxyBrowserCapture.DefaultProbeHost;
        var isFirefox = name.Equals("firefox", StringComparison.OrdinalIgnoreCase);
        var profileDir = isFirefox
            ? SystemProxyBrowserCapture.CreateTempFirefoxProfile($"twp-probe-{name}-")
            : Path.Combine(Path.GetTempPath(), $"twp-probe-{name}-" + Guid.NewGuid().ToString("N"));
        if (!isFirefox)
            Directory.CreateDirectory(profileDir);

        Process? proc = null;
        try
        {
            if (isFirefox)
            {
                // Leftover Firefox often swallows -new-instance / ignores a new profile.
                SystemProxyBrowserCapture.KillFirefoxProcesses();
                await Task.Delay(800).ConfigureAwait(true);
            }

            // Ensure system proxy is on (prior browser's off-check leaves it disabled until finally).
            await harness.OnUiAsync(() =>
            {
                if (!harness.ViewModel.SystemProxy)
                    harness.Robot.SetCheck("SystemProxyCheck", true);
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);

            SessionSnapshot? captured = null;
            void OnSession(object? _, SessionSnapshot s)
            {
                if (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase))
                    captured = s;
            }

            harness.Interception.SessionCaptured += OnSession;
            try
            {
                proc = isFirefox
                    ? SystemProxyBrowserCapture.StartFirefox(path, profileDir, $"https://{host}/")
                    : SystemProxyBrowserCapture.StartChromiumViaSystemProxy(
                        path, profileDir, $"https://{host}/", disableQuic: true);

                var deadline = DateTime.UtcNow + timeout;
                while (captured is null && DateTime.UtcNow < deadline)
                    await Task.Delay(250).ConfigureAwait(true);

                if (captured is null)
                {
                    log.Step($"proxy-{name}", false, $"No session for {host} within {timeout.TotalSeconds:0}s");
                    return false;
                }

                log.Step($"proxy-{name}", true, captured.Url);
            }
            finally
            {
                harness.Interception.SessionCaptured -= OnSession;
            }

            await harness.OnUiAsync(() =>
            {
                if (harness.ViewModel.SystemProxy)
                    harness.Robot.SetCheck("SystemProxyCheck", false);
            }).ConfigureAwait(true);
            await Task.Delay(800).ConfigureAwait(true);
            SystemProxyBrowserCapture.TryKill(proc);
            if (isFirefox)
                SystemProxyBrowserCapture.KillFirefoxProcesses();
            proc = null;

            SessionSnapshot? after = null;
            void OnAfter(object? _, SessionSnapshot s)
            {
                if (s.Url.Contains("after-disable", StringComparison.OrdinalIgnoreCase))
                    after = s;
            }

            harness.Interception.SessionCaptured += OnAfter;
            try
            {
                proc = isFirefox
                    ? SystemProxyBrowserCapture.StartFirefox(path, profileDir, $"https://{host}/?after-disable=1")
                    : SystemProxyBrowserCapture.StartChromiumViaSystemProxy(
                        path, profileDir, $"https://{host}/?after-disable=1", disableQuic: true);

                var offDeadline = DateTime.UtcNow.AddSeconds(12);
                while (after is null && DateTime.UtcNow < offDeadline)
                    await Task.Delay(250).ConfigureAwait(true);

                if (after is not null)
                {
                    log.Step($"proxy-{name}-off", false, "Still captured after system proxy disable");
                    return false;
                }

                log.Step($"proxy-{name}-off", true, "no after-disable capture");
                return true;
            }
            finally
            {
                harness.Interception.SessionCaptured -= OnAfter;
                await harness.OnUiAsync(() =>
                {
                    if (!harness.ViewModel.SystemProxy)
                        harness.Robot.SetCheck("SystemProxyCheck", true);
                }).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            log.Step($"proxy-{name}", false, ex.Message);
            return false;
        }
        finally
        {
            SystemProxyBrowserCapture.TryKill(proc);
            if (isFirefox)
                SystemProxyBrowserCapture.KillFirefoxProcesses();
            SystemProxyBrowserCapture.TryDeleteDir(profileDir);
        }
    }

    private static List<(string Name, string Path)> ResolveBrowsers(string browser)
    {
        if (browser.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return BrowserPaths.ResolveAuto().ToList();

        var path = BrowserPaths.Resolve(browser);
        return path is null ? [] : [(browser.ToLowerInvariant(), path)];
    }
}
