using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Firefox often ignores GNOME/KDE system proxy. Write manual <c>network.proxy.*</c> prefs into
///     the default profile and quit/relaunch so enable/disable takes effect immediately.
/// </summary>
internal static class LinuxFirefoxProxy
{
    private const string BackupFileName = "firefox-proxy-backup.json";
    private const string MarkerPref = "titanium.inspector.proxy.managed";

    private static readonly Regex UserPrefLine = new(
        @"^\s*user_pref\(\s*""(?<key>[^""]+)""\s*,\s*(?<value>.+?)\s*\)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Applies manual proxy prefs; returns true when prefs were written.</summary>
    internal static bool Apply(string hostname, int port, string? winInetProxyOverride = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (string.IsNullOrWhiteSpace(hostname) || port <= 0)
            return false;
        if (!FirefoxCertificateTrust.TryResolveDefaultProfileDirectory(out var profileDir, out _))
            return false;

        var prefsPath = Path.Combine(profileDir, "prefs.js");
        try
        {
            Directory.CreateDirectory(profileDir);
            var existing = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : string.Empty;
            BackupIfNeeded(existing);

            var bypass = BuildFirefoxBypassList(winInetProxyOverride);
            var managed = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["network.proxy.type"] = "1",
                ["network.proxy.http"] = JsonSerializer.Serialize(hostname),
                ["network.proxy.http_port"] = port.ToString(),
                ["network.proxy.ssl"] = JsonSerializer.Serialize(hostname),
                ["network.proxy.ssl_port"] = port.ToString(),
                ["network.proxy.share_proxy_settings"] = "true",
                ["network.proxy.no_proxies_on"] = JsonSerializer.Serialize(bypass),
                [MarkerPref] = "true",
            };

            File.WriteAllText(prefsPath, MergePrefs(existing, managed));
            TryRelaunchFirefox(enableProxy: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores prior prefs (or clears managed manual proxy) and relaunches Firefox if needed.</summary>
    internal static void Clear()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!FirefoxCertificateTrust.TryResolveDefaultProfileDirectory(out var profileDir, out _))
        {
            DeleteBackup();
            return;
        }

        var prefsPath = Path.Combine(profileDir, "prefs.js");
        try
        {
            if (!File.Exists(prefsPath))
            {
                DeleteBackup();
                return;
            }

            var existing = File.ReadAllText(prefsPath);
            if (!existing.Contains(MarkerPref, StringComparison.Ordinal) && !HasBackup())
                return;

            string restored;
            if (TryReadBackup(out var backup) && backup.Count > 0)
            {
                // Remove managed keys then re-apply snapshotted values (missing key = delete).
                var withoutManaged = RemoveKeys(existing,
                [
                    "network.proxy.type",
                    "network.proxy.http",
                    "network.proxy.http_port",
                    "network.proxy.ssl",
                    "network.proxy.ssl_port",
                    "network.proxy.share_proxy_settings",
                    "network.proxy.no_proxies_on",
                    MarkerPref,
                ]);
                restored = MergePrefs(withoutManaged, backup);
                restored = RemoveKeys(restored, [MarkerPref]);
            }
            else
            {
                // No backup: fall back to system proxy settings.
                restored = MergePrefs(RemoveKeys(existing, [MarkerPref]), new Dictionary<string, string>
                {
                    ["network.proxy.type"] = "5",
                });
            }

            File.WriteAllText(prefsPath, restored);
            DeleteBackup();
            TryRelaunchFirefox(enableProxy: false);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Test hook: merge managed prefs into prefs.js text.</summary>
    internal static string MergePrefsForTests(string existing, IReadOnlyDictionary<string, string> managed) =>
        MergePrefs(existing, managed);

    /// <summary>Test hook: remove keys from prefs.js text.</summary>
    internal static string RemoveKeysForTests(string existing, IEnumerable<string> keys) =>
        RemoveKeys(existing, keys);

    /// <summary>Test hook: Firefox bypass list formatting.</summary>
    internal static string BuildFirefoxBypassListForTests(string? winInetProxyOverride) =>
        BuildFirefoxBypassList(winInetProxyOverride);

    private static string BuildFirefoxBypassList(string? winInetProxyOverride)
    {
        var hosts = UnixProxyBypassMapper.ToUnixBypassHosts(winInetProxyOverride).ToList();
        if (hosts.Count == 0)
            return "localhost, 127.0.0.1";
        return string.Join(", ", hosts);
    }

    private static string MergePrefs(string existing, IReadOnlyDictionary<string, string> values)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(existing ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = UserPrefLine.Match(line);
            if (match.Success)
            {
                var key = match.Groups["key"].Value;
                if (values.TryGetValue(key, out var replacement))
                {
                    lines.Add($"user_pref(\"{key}\", {replacement});");
                    seen.Add(key);
                    continue;
                }
            }

            lines.Add(line);
        }

        foreach (var (key, value) in values)
        {
            if (seen.Contains(key))
                continue;
            lines.Add($"user_pref(\"{key}\", {value});");
        }

        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.AppendLine(l);
        return sb.ToString();
    }

    private static string RemoveKeys(string existing, IEnumerable<string> keys)
    {
        var remove = new HashSet<string>(keys, StringComparer.Ordinal);
        var sb = new StringBuilder();
        using var reader = new StringReader(existing ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = UserPrefLine.Match(line);
            if (match.Success && remove.Contains(match.Groups["key"].Value))
                continue;
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static void BackupIfNeeded(string existingPrefs)
    {
        if (HasBackup())
            return;

        var keys =
            new[]
            {
                "network.proxy.type",
                "network.proxy.http",
                "network.proxy.http_port",
                "network.proxy.ssl",
                "network.proxy.ssl_port",
                "network.proxy.share_proxy_settings",
                "network.proxy.no_proxies_on",
            };
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(existingPrefs ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = UserPrefLine.Match(line);
            if (!match.Success)
                continue;
            var key = match.Groups["key"].Value;
            if (keys.Contains(key, StringComparer.Ordinal))
                snapshot[key] = match.Groups["value"].Value.Trim();
        }

        try
        {
            var path = BackupPath();
            if (path is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // ignore
        }
    }

    private static bool TryReadBackup(out Dictionary<string, string> backup)
    {
        backup = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = BackupPath();
            if (path is null || !File.Exists(path))
                return false;
            var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (doc is null)
                return false;
            backup = new Dictionary<string, string>(doc, StringComparer.Ordinal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasBackup()
    {
        var path = BackupPath();
        return path is not null && File.Exists(path);
    }

    private static void DeleteBackup()
    {
        try
        {
            var path = BackupPath();
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string? BackupPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        return Path.Combine(home, ".config", "TitaniumInspector", BackupFileName);
    }

    private static void TryRelaunchFirefox(bool enableProxy)
    {
        try
        {
            var wasRunning = FirefoxCertificateTrust.IsFirefoxProcessRunning();
            if (!wasRunning)
                return;

            FirefoxCertificateTrust.TryRequestFirefoxQuit(TimeSpan.FromSeconds(8));

            // Force remaining firefox processes if still up (proxy switch must not leave stale prefs in memory).
            if (FirefoxCertificateTrust.IsFirefoxProcessRunning())
            {
                foreach (var name in new[] { "firefox", "firefox-bin" })
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                            finally { p.Dispose(); }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            var launch = ResolveFirefoxLaunch();
            if (string.IsNullOrEmpty(launch))
                return;

            var display = LinuxGraphicalSession.TryGetDisplay()
                          ?? Environment.GetEnvironmentVariable("DISPLAY")
                          ?? string.Empty;
            var dbus = LinuxGraphicalSession.TryGetDbusSessionAddress()
                       ?? Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
                       ?? string.Empty;
            if (!LinuxGraphicalSession.IsUsableDbusAddress(dbus))
                dbus = string.Empty;
            var xauth = Environment.GetEnvironmentVariable("XAUTHORITY") ?? string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName = launch,
                Arguments = "--new-instance",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrWhiteSpace(display))
                psi.Environment["DISPLAY"] = display;
            if (!string.IsNullOrWhiteSpace(dbus))
                psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = dbus;
            if (!string.IsNullOrWhiteSpace(xauth))
                psi.Environment["XAUTHORITY"] = xauth;

            // Detach so Inspector SIGTERM does not kill the new Firefox.
            var scriptPath = Path.Combine(Path.GetTempPath(),
                $"titanium-firefox-relaunch-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
            var script = $"""
                #!/bin/bash
                set +e
                [ -n {ShellQuote(display)} ] && export DISPLAY={ShellQuote(display)}
                [ -n {ShellQuote(dbus)} ] && export DBUS_SESSION_BUS_ADDRESS={ShellQuote(dbus)}
                [ -n {ShellQuote(xauth)} ] && export XAUTHORITY={ShellQuote(xauth)}
                {ShellQuote(launch)} --new-instance >/dev/null 2>&1 &
                rm -f -- {ShellQuote(scriptPath)}
                """;
            _ = enableProxy; // reserved for future proxy-specific launch flags
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
        }
        catch
        {
            // best-effort
        }
    }

    private static string? ResolveFirefoxLaunch()
    {
        foreach (var candidate in new[]
                 {
                     "/usr/bin/firefox",
                     "/usr/bin/firefox-esr",
                     "/snap/bin/firefox",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ShellQuote(string value) =>
        "'" + (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
