using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Chrome/Chromium/Edge ignore live Preference file edits and often ignore gsettings.
///     When system proxy is toggled, quit and relaunch each running Chromium-family browser
///     with session restore so traffic switches immediately.
/// </summary>
internal static class LinuxChromiumRelaunch
{
    private enum BrowserFamily
    {
        Chrome,
        Chromium,
        Brave,
        Edge,
    }

    private static readonly (BrowserFamily Family, string[] Binaries)[] FamilyLaunchers =
    [
        (BrowserFamily.Chrome,
        [
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
            "/opt/google/chrome/google-chrome",
        ]),
        (BrowserFamily.Chromium,
        [
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
        ]),
        (BrowserFamily.Brave,
        [
            "/usr/bin/brave-browser",
        ]),
        (BrowserFamily.Edge,
        [
            "/usr/bin/microsoft-edge-stable",
            "/usr/bin/microsoft-edge",
        ]),
    ];

    /// <summary>
    ///     If a Chromium-family browser is running, quit it and relaunch with optional proxy flags
    ///     and <c>--restore-last-session</c>. Returns true when a relaunch was scheduled.
    ///     Runs detached (<c>setsid</c>) so SIGTERM/app exit cannot abort mid-quit/relaunch.
    ///     Relaunches each running browser family with that family's own binary (not always Chrome).
    /// </summary>
    internal static bool TryRelaunchForProxyChange(string? hostname, int port, bool enableProxy)
    {
        var mains = FindMainBrowsers().ToList();
        if (mains.Count == 0)
            return false;

        var families = mains.Select(m => m.Family).Distinct().OrderBy(f => f).ToList();
        var launchLines = new List<string>();
        foreach (var family in families)
        {
            var launch = ResolveLaunchBinary(family);
            if (string.IsNullOrEmpty(launch))
                continue;
            launchLines.Add(launch);
        }

        if (launchLines.Count == 0)
            return false;

        var args = "--restore-last-session --disable-quic";
        if (enableProxy && !string.IsNullOrWhiteSpace(hostname) && port > 0)
            args = LinuxBrowserLaunchProxy.ChromeProxyFlags(hostname, port) + " " + args;

        var display = LinuxGraphicalSession.TryGetDisplay()
                      ?? Environment.GetEnvironmentVariable("DISPLAY")
                      ?? string.Empty;
        var dbus = LinuxGraphicalSession.TryGetDbusSessionAddress()
                   ?? Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
                   ?? string.Empty;
        if (!LinuxGraphicalSession.IsUsableDbusAddress(dbus))
            dbus = string.Empty;
        var xauth = Environment.GetEnvironmentVariable("XAUTHORITY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(xauth))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidate = Path.Combine(home, ".Xauthority");
            if (File.Exists(candidate))
                xauth = candidate;
        }

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"titanium-chrome-relaunch-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
            var pidList = string.Join(" ", mains.Select(m => m.Pid));
            var quitCmds = string.Join("\n", launchLines.Select(l =>
                $"{ShellQuote(l)} --quit >/dev/null 2>&1 || true"));
            var startCmds = string.Join("\n", launchLines.Select(l =>
                $"# shellcheck disable=SC2086\n{ShellQuote(l)} $ARGS >/dev/null 2>&1 &"));

            var script = $$"""
                #!/bin/bash
                set +e
                ARGS={{ShellQuote(args)}}
                DISPLAY_VAL={{ShellQuote(display)}}
                DBUS_VAL={{ShellQuote(dbus)}}
                XAUTH_VAL={{ShellQuote(xauth)}}
                ENABLE={{(enableProxy ? "1" : "0")}}
                PIDS="{{pidList}}"
                [ -n "$DISPLAY_VAL" ] && export DISPLAY="$DISPLAY_VAL"
                [ -n "$DBUS_VAL" ] && export DBUS_SESSION_BUS_ADDRESS="$DBUS_VAL"
                [ -n "$XAUTH_VAL" ] && export XAUTHORITY="$XAUTH_VAL"
                {{quitCmds}}
                for i in $(seq 1 40); do
                  alive=0
                  for p in $PIDS; do [ -d "/proc/$p" ] && alive=1; done
                  [ "$alive" = "0" ] && break
                  sleep 0.2
                done
                find_mains() {
                  for d in /proc/[0-9]*; do
                    pid=${d##*/}
                    raw=$(tr '\0' ' ' < "$d/cmdline" 2>/dev/null)
                    [ -z "$raw" ] && continue
                    case "$raw" in
                      *--type=*|*crashpad*|*nacl_helper*|*devtools-mcp*|*chrome-devtools-mcp*|*/cursor/*) continue ;;
                    esac
                    exe=${raw%% *}
                    base=${exe##*/}
                    case "$exe" in
                      /opt/google/chrome/chrome|/usr/lib/chromium-browser/chromium-browser|/usr/lib/chromium/chromium|/usr/lib/brave.com/brave/brave|/opt/brave.com/brave/brave|/opt/microsoft/msedge/msedge) echo "$pid" ;;
                      *) case "$base" in chrome|chromium|chromium-browser|brave|msedge) echo "$pid" ;; esac ;;
                    esac
                  done
                }
                for p in $(find_mains); do kill -TERM "$p" 2>/dev/null || true; done
                sleep 1
                for p in $(find_mains); do kill -KILL "$p" 2>/dev/null || true; done
                sleep 0.5
                {{startCmds}}
                if [ "$ENABLE" = "0" ]; then
                  pidf="$HOME/.config/TitaniumInspector/fail-open-proxy.pid"
                  if [ -f "$pidf" ]; then
                    kill "$(cat "$pidf")" 2>/dev/null || true
                    rm -f "$pidf"
                  fi
                  pkill -f 'fail-open-proxy.py' 2>/dev/null || true
                fi
                rm -f -- {{ShellQuote(scriptPath)}}
                """;
            File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(scriptPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // best-effort
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/setsid",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-f", "/bin/bash", scriptPath },
            })?.Dispose();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Test hook: resolve launch binary for a running executable path.</summary>
    internal static string? ResolveLaunchBinaryForExeForTests(string exe) =>
        TryClassify(exe, out var family) ? ResolveLaunchBinary(family) : null;

    /// <summary>Test hook: classify main browser executable into a family name.</summary>
    internal static string? ClassifyFamilyForTests(string exe) =>
        TryClassify(exe, out var family) ? family.ToString() : null;

    private static string? ResolveLaunchBinary(BrowserFamily family)
    {
        var entry = FamilyLaunchers.FirstOrDefault(f => f.Family == family);
        return entry.Binaries?.FirstOrDefault(File.Exists);
    }

    private static string ShellQuote(string value) =>
        "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static IEnumerable<(int Pid, BrowserFamily Family)> FindMainBrowsers()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid <= 1)
                continue;

            string raw;
            try
            {
                raw = File.ReadAllText(Path.Combine(dir, "cmdline"));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(raw))
                continue;

            var full = raw.Replace('\0', ' ').Trim();
            var exe = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (exe.Contains(' ', StringComparison.Ordinal))
                exe = exe.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

            if (string.IsNullOrEmpty(exe))
                continue;

            if (full.Contains("--type=", StringComparison.Ordinal) ||
                exe.Contains("crashpad", StringComparison.OrdinalIgnoreCase) ||
                exe.Contains("nacl_helper", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("devtools-mcp", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("chrome-devtools-mcp", StringComparison.OrdinalIgnoreCase) ||
                exe.Contains("/cursor/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryClassify(exe, out var family))
                yield return (pid, family);
        }
    }

    private static bool TryClassify(string exe, out BrowserFamily family)
    {
        family = default;
        var name = Path.GetFileName(exe);

        if (exe is "/opt/microsoft/msedge/msedge" ||
            name is "msedge" ||
            exe.Contains("microsoft-edge", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("/msedge/", StringComparison.OrdinalIgnoreCase))
        {
            family = BrowserFamily.Edge;
            return true;
        }

        if (exe.Contains("brave", StringComparison.OrdinalIgnoreCase) || name is "brave")
        {
            family = BrowserFamily.Brave;
            return true;
        }

        if (exe is "/usr/lib/chromium-browser/chromium-browser" or "/usr/lib/chromium/chromium" ||
            name is "chromium" or "chromium-browser")
        {
            family = BrowserFamily.Chromium;
            return true;
        }

        if (exe is "/opt/google/chrome/chrome" || name is "chrome")
        {
            family = BrowserFamily.Chrome;
            return true;
        }

        return false;
    }
}
