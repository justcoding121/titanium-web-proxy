using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Chrome/Chromium ignore live Preference file edits and often ignore gsettings on this stack.
///     When system proxy is toggled, quit and relaunch Chromium-family browsers with session restore
///     so traffic switches immediately for already-open windows.
/// </summary>
internal static class LinuxChromiumRelaunch
{
    private static readonly string[] LaunchBinaries =
    [
        "/usr/bin/google-chrome-stable",
        "/usr/bin/google-chrome",
        "/opt/google/chrome/google-chrome",
        "/usr/bin/chromium-browser",
        "/usr/bin/chromium",
        "/usr/bin/brave-browser",
        "/usr/bin/microsoft-edge-stable",
    ];

    /// <summary>
    ///     If a Chromium-family browser is running, quit it and relaunch with optional proxy flags
    ///     and <c>--restore-last-session</c>. Returns true when a relaunch was scheduled.
    ///     Runs detached (<c>setsid</c>) so SIGTERM/app exit cannot abort mid-quit/relaunch.
    /// </summary>
    internal static bool TryRelaunchForProxyChange(string? hostname, int port, bool enableProxy)
    {
        var mains = FindMainBrowserPids().ToList();
        if (mains.Count == 0)
            return false;

        var launch = LaunchBinaries.FirstOrDefault(File.Exists);
        if (string.IsNullOrEmpty(launch))
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

        // Detached helper survives Inspector SIGTERM (in-process waits were killed mid-relaunch).
        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"titanium-chrome-relaunch-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
            var pidList = string.Join(" ", mains);
            var script = $$"""
                #!/bin/bash
                set +e
                LAUNCH={{ShellQuote(launch)}}
                ARGS={{ShellQuote(args)}}
                DISPLAY_VAL={{ShellQuote(display)}}
                DBUS_VAL={{ShellQuote(dbus)}}
                XAUTH_VAL={{ShellQuote(xauth)}}
                ENABLE={{(enableProxy ? "1" : "0")}}
                PIDS="{{pidList}}"
                [ -n "$DISPLAY_VAL" ] && export DISPLAY="$DISPLAY_VAL"
                [ -n "$DBUS_VAL" ] && export DBUS_SESSION_BUS_ADDRESS="$DBUS_VAL"
                [ -n "$XAUTH_VAL" ] && export XAUTHORITY="$XAUTH_VAL"
                "$LAUNCH" --quit >/dev/null 2>&1 || true
                for i in $(seq 1 40); do
                  alive=0
                  for p in $PIDS; do [ -d "/proc/$p" ] && alive=1; done
                  [ "$alive" = "0" ] && break
                  sleep 0.2
                done
                # Match main chrome (space-joined cmdline) — exclude helpers / MCP
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
                # shellcheck disable=SC2086
                "$LAUNCH" $ARGS >/dev/null 2>&1 &
                if [ "$ENABLE" = "0" ]; then
                  # Drop fail-open listener once browser is direct again
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

    private static string ShellQuote(string value) =>
        "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static IEnumerable<int> FindMainBrowserPids()
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

            // Some environments deliver cmdline as a single space-joined argv[0]; normalize.
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

            if (IsChromiumMainExecutable(exe))
                yield return pid;
        }
    }

    private static bool IsChromiumMainExecutable(string exe)
    {
        // Exact well-known binaries (avoid matching chrome_crashpad_handler via prefix).
        if (exe is "/opt/google/chrome/chrome" or
            "/usr/lib/chromium-browser/chromium-browser" or
            "/usr/lib/chromium/chromium" or
            "/usr/lib/brave.com/brave/brave" or
            "/opt/brave.com/brave/brave" or
            "/opt/microsoft/msedge/msedge")
            return true;

        var name = Path.GetFileName(exe);
        return name is "chrome" or "chromium" or "chromium-browser" or "brave" or "msedge";
    }
}
