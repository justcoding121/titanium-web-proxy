using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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
        Opera,
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
        (BrowserFamily.Opera,
        [
            "/usr/bin/opera",
            "/usr/bin/opera-stable",
        ]),
    ];

    /// <summary>
    ///     If a Chromium-family browser is running, quit it and relaunch with optional proxy flags
    ///     and <c>--restore-last-session</c>. Returns true when a relaunch was scheduled.
    ///     Runs detached (<c>setsid</c>) so SIGTERM/app exit cannot abort mid-quit/relaunch.
    ///     Relaunches each running browser family with that family's own binary (not always Chrome).
    /// </summary>
    internal static bool TryRelaunchForProxyChange(
        string? hostname, int port, bool enableProxy, bool proxyLoopback = false)
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

        var args = "--restore-last-session";
        if (enableProxy && !string.IsNullOrWhiteSpace(hostname) && port > 0)
            args = LinuxBrowserLaunchProxy.ChromeProxyFlags(hostname, port, proxyLoopback) + " " + args;

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
            var script = BuildRelaunchScript(scriptPath, mains, launchLines, args, display, dbus, xauth, enableProxy);
            File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));
            TryMakeExecutable(scriptPath);
            StartDetachedScript(scriptPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     After a browser quits it writes the proxy it had in memory. This puts
    ///     loopback <c>fixed_servers</c> profiles back on the desktop proxy.
    /// </summary>
    private static string LoopbackStripScriptB64() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """
            import json, os
            home = os.path.expanduser("~")
            roots = (
                ".config/google-chrome",
                ".config/google-chrome-beta",
                ".config/google-chrome-unstable",
                ".config/chromium",
                ".config/microsoft-edge",
                ".config/BraveSoftware/Brave-Browser",
                ".config/opera",
                "snap/chromium/common/chromium",
                "snap/microsoft-edge/common/microsoft-edge",
                ".var/app/com.google.Chrome/config/google-chrome",
                ".var/app/org.chromium.Chromium/config/chromium",
                ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser",
                ".var/app/com.microsoft.Edge/config/microsoft-edge",
            )
            for rel in roots:
                root = os.path.join(home, rel)
                if not os.path.isdir(root):
                    continue
                names = ["Default"]
                try:
                    names += [n for n in os.listdir(root) if n.startswith("Profile ")]
                except OSError:
                    pass
                for name in names:
                    path = os.path.join(root, name, "Preferences")
                    if not os.path.isfile(path):
                        continue
                    try:
                        doc = json.load(open(path, encoding="utf-8"))
                    except (OSError, ValueError):
                        continue
                    proxy = doc.get("proxy")
                    if not isinstance(proxy, dict) or proxy.get("mode") != "fixed_servers":
                        continue
                    server = str(proxy.get("server") or "")
                    if not any(h in server for h in ("127.0.0.1", "localhost", "[::1]")):
                        continue
                    doc["proxy"] = {"mode": "system"}
                    tmp = path + ".tmp"
                    try:
                        json.dump(doc, open(tmp, "w", encoding="utf-8"), separators=(",", ":"))
                        os.replace(tmp, path)
                    except OSError:
                        pass
            """));

    private static string BuildRelaunchScript( // NOSONAR S107 -- Script fields map 1:1 onto the generated shell.
        string scriptPath,
        List<(int Pid, BrowserFamily Family)> mains,
        List<string> launchLines,
        string args,
        string display,
        string dbus,
        string xauth,
        bool enableProxy)
    {
        var pidList = string.Join(" ", mains.Select(m => m.Pid));
        var quitCmds = string.Join("\n", launchLines.Select(l =>
            $"{ShellQuote(l)} --quit >/dev/null 2>&1 || true"));
        var startCmds = string.Join("\n", launchLines.Select(l =>
            $"# shellcheck disable=SC2086\n{ShellQuote(l)} $ARGS >/dev/null 2>&1 &"));

        return $$"""
                #!/bin/bash
                set +e
                exec 9>/tmp/titanium-chromium-relaunch.lock
                flock -w 30 9 || exit 0
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
                    raw=$(tr '\0' ' ' < "$d/cmdline" 2>/dev/null || true)
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
                # Quit flushes the in-memory fixed_servers entry back over the file we
                # cleared. Strip again after the process is gone, then start.
                python3 -c "import base64,sys; exec(base64.b64decode(sys.argv[1]))" {ShellQuote(LoopbackStripScriptB64())} || true
                {{startCmds}}
                # Leave the fail-open helper running. File → Exit stops Inspector, but
                # programs that already inherited http://127.0.0.1:8866 still dial that
                # port. Killing the helper here is what drops the network. The login
                # script removes it only after those programs are gone.
                rm -f -- {{ShellQuote(scriptPath)}}
                """;
    }

    private static void TryMakeExecutable(string scriptPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
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

    private static void StartDetachedScript(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/setsid",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-f", "/bin/bash", scriptPath },
        })?.Dispose();
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

    private static IEnumerable<(int Pid, BrowserFamily Family)> FindMainBrowsers() // NOSONAR S3776 -- /proc walk identifies Chromium mains without extra process snapshots.
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

        if (exe.Contains("brave", StringComparison.OrdinalIgnoreCase) || name is "brave" or "brave-browser")
        {
            family = BrowserFamily.Brave;
            return true;
        }

        if (exe.Contains("/opera", StringComparison.OrdinalIgnoreCase) ||
            name is "opera" or "opera-stable")
        {
            family = BrowserFamily.Opera;
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
