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
                $"{headlessArg}{quic}--no-first-run --disable-fre --no-default-browser-check --disable-extensions --disable-component-update " +
                "--disable-background-networking --disable-sync " +
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
    public static Process StartSafari(string url) => StartSafari(url, fresh: false);

    /// <summary>
    /// <paramref name="fresh"/> passes <c>open -F</c> so Safari does not restore a tab
    /// that already raced HTTP/3 past the proxy.
    /// </summary>
    public static Process StartSafari(string url, bool fresh)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Safari probe is macOS-only");

        TryDisableSafariHttp3();

        var arguments = fresh ? $"-F -a Safari \"{url}\"" : $"-a Safari \"{url}\"";
        return Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            Arguments = arguments,
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

        TryKillall(force: false);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && SafariFamilyStillRunning())
            Thread.Sleep(200);

        // A networking process that survived the upgrade pass keeps the old root
        // evaluation and any HTTP/3 alternative service. SIGTERM is not enough then.
        if (SafariFamilyStillRunning())
        {
            TryKillall(force: true);
            deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && SafariFamilyStillRunning())
                Thread.Sleep(200);
        }

        // HTTP/3 is on by default and ignores the old feature flag. A cached
        // alternative service sends the GET around the proxy while CONNECT
        // still shows up in the HAR. The store is only safe to delete once
        // WebKit.Networking has exited.
        ClearSafariAlternativeServices();
    }

    private static void TryKillall(bool force)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "killall",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            if (force)
                start.ArgumentList.Add("-9");
            foreach (var name in new[]
            {
                "Safari",
                "com.apple.WebKit.Networking",
                "com.apple.WebKit.WebContent",
                "com.apple.WebKit.GPU",
            })
            {
                start.ArgumentList.Add(name);
            }

            using var kill = Process.Start(start);
            kill?.WaitForExit(3000);
        }
        catch
        {
            // already gone
        }
    }

    private static bool SafariFamilyStillRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (name.Contains("Safari", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("WebKit.Networking", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("WebKit.WebContent", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("WebKit.GPU", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes WebKit's alternative-service database so the next Safari launch
    /// cannot reuse an HTTP/3 route learned before this proxy's certificate existed.
    /// </summary>
    private static void ClearSafariAlternativeServices()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[]
        {
            Path.Combine(home, "Library", "Containers", "com.apple.Safari", "Data"),
            Path.Combine(home, "Library", "Containers", "com.apple.WebKit.Networking", "Data"),
            Path.Combine(home, "Library", "Group Containers", "group.com.apple.Safari"),
            Path.Combine(home, "Library", "Caches", "com.apple.Safari"),
            Path.Combine(home, "Library", "Caches", "WebKit"),
            Path.Combine(home, "Library", "HTTPStorages"),
        })
        {
            DeleteAlternativeServiceFiles(root);
        }
    }

    private static void DeleteAlternativeServiceFiles(string root)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "AlternativeService.sqlite*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* locked or already gone */ }
            }

            foreach (var dir in Directory.EnumerateDirectories(root, "AlternativeServices", SearchOption.AllDirectories))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* locked or already gone */ }
            }
        }
        catch
        {
            // Cache layout differs across macOS versions; a miss is not fatal.
        }
    }

    /// <summary>
    /// HTTP/3 (QUIC) bypasses HTTP(S) proxies. Chromium gets --disable-quic; Safari has no flag.
    /// A CONNECT that never becomes a GET is what that race looks like in the HAR.
    /// </summary>
    private static void TryDisableSafariHttp3()
    {
        foreach (var domain in new[]
        {
            "com.apple.Safari",
            "com.apple.Safari.SandboxBroker",
            "com.apple.WebKit.Networking",
        })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "defaults",
                    Arguments = $"write {domain} WebKitPreferences.http3Enabled -bool false",
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
