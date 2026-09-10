using System.Diagnostics;
using System.Text;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Shared;

/// <summary>
/// Launch a browser that must honor the OS system proxy (no --proxy-server on Windows).
/// Shared by InspectorDesktopProbe and E2E-Slow.
/// </summary>
public static class SystemProxyBrowserCapture
{
    public const string DefaultProbeHost = "example.com";

    public static Process StartChromiumViaSystemProxy(
        string browserPath,
        string userDataDir,
        string url,
        bool disableQuic,
        bool headless = false)
    {
        var quic = disableQuic ? "--disable-quic " : string.Empty;
        var headlessArg = headless ? "--headless=new --disable-gpu " : string.Empty;
        return Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments =
                $"{headlessArg}{quic}--no-first-run --disable-extensions --disable-background-networking " +
                $"--user-data-dir=\"{userDataDir}\" " +
                $"\"{url}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {browserPath}");
    }

    /// <summary>
    /// Launch Firefox with an isolated profile that forces OS system proxy
    /// (<c>network.proxy.type=5</c>). Callers should pass a dir from
    /// <see cref="CreateTempFirefoxProfile"/> and delete it afterward.
    /// </summary>
    public static Process StartFirefox(string browserPath, string profileDir, string url, bool headless = false)
    {
        var headlessArg = headless ? "--headless " : string.Empty;
        return Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = $"{headlessArg}-no-remote -profile \"{profileDir}\" \"{url}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start {browserPath}");
    }

    /// <summary>
    /// Fresh Firefox profile with system-proxy prefs (parity with Chromium's temp user-data-dir).
    /// Avoids a dirty default profile that may ignore WinINET / gsettings.
    /// </summary>
    public static string CreateTempFirefoxProfile(string? prefix = null)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            (prefix ?? "twp-ff-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // type 5 = use system proxy settings (Windows WinINET / macOS / Linux DE).
        File.WriteAllText(
            Path.Combine(dir, "user.js"),
            """
            user_pref("network.proxy.type", 5);
            user_pref("network.proxy.share_proxy_settings", true);
            user_pref("network.proxy.allow_hijacking_localhost", true);
            user_pref("network.http.http3.enable", false);
            user_pref("network.trr.mode", 5);
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.startup.homepage_override.mstone", "ignore");
            user_pref("toolkit.telemetry.enabled", false);
            user_pref("app.update.enabled", false);
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("startup.homepage_welcome_url", "");
            user_pref("startup.homepage_welcome_url.additional", "");
            """,
            Encoding.UTF8);
        return dir;
    }

    /// <summary>
    /// Launch Safari via Launch Services so it uses macOS system proxy (no CLI proxy flags).
    /// <c>open</c> exits immediately; call <see cref="KillSafariProcesses"/> to stop Safari.
    /// </summary>
    public static Process StartSafari(string url)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Safari probe is macOS-only");

        TryDisableSafariHttp3();

        return Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            Arguments = $"-a Safari \"{url}\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start Safari");
    }

    public static void KillSafariProcesses()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            using var quit = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", "tell application \"Safari\" to quit" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            quit?.WaitForExit(5000);
        }
        catch
        {
            // ignore
        }

        foreach (var p in Process.GetProcessesByName("Safari"))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// HTTP/3 (QUIC) bypasses HTTP(S) proxies. Chromium gets --disable-quic; Safari has no flag.
    /// </summary>
    private static void TryDisableSafariHttp3()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "defaults",
                Arguments = "write com.apple.Safari WebKitPreferences.http3Enabled -bool false",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            p?.WaitForExit(3000);
        }
        catch
        {
            // ignore
        }
    }

    public static void KillFirefoxProcesses()
    {
        foreach (var p in Process.GetProcessesByName("firefox"))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }

    public static async Task<SessionSnapshot?> WaitForHostAsync(
        InterceptionService interception,
        string host,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        SessionSnapshot? captured = null;
        void Handler(object? _, SessionSnapshot s)
        {
            if (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase))
                captured = s;
        }

        interception.SessionCaptured += Handler;
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (captured is null && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            return captured;
        }
        finally
        {
            interception.SessionCaptured -= Handler;
        }
    }

    public static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    public static void TryDeleteDir(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
