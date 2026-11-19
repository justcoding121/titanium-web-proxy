using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Write Firefox <c>network.proxy.type</c> 5 so new connections follow the system proxy.
///     Prefs are not hot-reloaded in a running Firefox; Inspector does not quit or relaunch it.
/// </summary>
internal static partial class LinuxFirefoxProxy
{
    private const string BackupFileName = "firefox-proxy-backup.json";
    private const string MozillaDirName = ".mozilla";
    private const string FirefoxDirName = "firefox";
    private const string MarkerPref = "titanium.inspector.proxy.managed";
    private const string NetworkProxyTypeKey = "network.proxy.type";
    private const string NetworkProxyHttpKey = "network.proxy.http";
    private const string NetworkProxyHttpPortKey = "network.proxy.http_port";
    private const string NetworkProxySslKey = "network.proxy.ssl";
    private const string NetworkProxySslPortKey = "network.proxy.ssl_port";
    private const string NetworkProxyShareKey = "network.proxy.share_proxy_settings";
    private const string NetworkProxyBypassKey = "network.proxy.no_proxies_on";
    private const string Http3EnabledKey = "network.http.http3.enabled";

    [GeneratedRegex(
        @"^\s*user_pref\(\s*""(?<key>[^""]+)""\s*,\s*(?<value>.+?)\s*\)\s*;\s*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex UserPrefLine();

    private static readonly string[] ManagedProxyKeys =
    [
        NetworkProxyTypeKey,
        NetworkProxyHttpKey,
        NetworkProxyHttpPortKey,
        NetworkProxySslKey,
        NetworkProxySslPortKey,
        NetworkProxyShareKey,
        NetworkProxyBypassKey,
        Http3EnabledKey,
        MarkerPref,
    ];

    private static readonly string[] SnapshotProxyKeys =
    [
        NetworkProxyTypeKey,
        NetworkProxyHttpKey,
        NetworkProxyHttpPortKey,
        NetworkProxySslKey,
        NetworkProxySslPortKey,
        NetworkProxyShareKey,
        NetworkProxyBypassKey,
        Http3EnabledKey,
    ];

    /// <summary>Applies manual proxy prefs; returns true when prefs were written.</summary>
    /// <param name="relaunch">
    ///     Quit and start Firefox once when it is already running. Callers that write prefs twice
    ///     pass false on the first write so Firefox is started only once.
    /// </param>
    internal static bool Apply(string hostname, int port, string? winInetProxyOverride = null, bool relaunch = true)
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
                [NetworkProxyTypeKey] = "5",
                [NetworkProxyHttpKey] = JsonSerializer.Serialize(hostname),
                [NetworkProxyHttpPortKey] = port.ToString(),
                [NetworkProxySslKey] = JsonSerializer.Serialize(hostname),
                [NetworkProxySslPortKey] = port.ToString(),
                [NetworkProxyShareKey] = "true",
                [NetworkProxyBypassKey] = JsonSerializer.Serialize(bypass),
                [Http3EnabledKey] = "false",
                [MarkerPref] = "true",
            };

            File.WriteAllText(prefsPath, MergePrefs(existing, managed));
            _ = relaunch;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores prior prefs (or clears managed manual proxy) and relaunches Firefox if needed.</summary>
    internal static void Clear() => Clear(relaunch: true);

    /// <summary>
    ///     Dead-app clear: strip managed prefs from every profile under the usual Firefox roots
    ///     (Classic / Snap / Flatpak), without relaunching.
    /// </summary>
    internal static void ClearInstalledHooks() => Clear(relaunch: false);

    private static void Clear(bool relaunch)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var cleared = false;
        foreach (var prefsPath in EnumeratePrefsJsPaths())
        {
            try
            {
                if (!File.Exists(prefsPath))
                    continue;

                var existing = File.ReadAllText(prefsPath);
                var hasMarker = existing.Contains(MarkerPref, StringComparison.Ordinal);
                var hasLoopback = PrefsPointAtLoopback(existing);
                if (!hasMarker && !hasLoopback)
                    continue;

                string restored;
                if (hasMarker && TryReadBackup(out var backup) && backup.Count > 0)
                {
                    var withoutManaged = RemoveKeys(existing, ManagedProxyKeys);
                    restored = MergePrefs(withoutManaged, backup);
                    restored = RemoveKeys(restored, [MarkerPref]);
                }
                else
                {
                    // No backup: fall back to system proxy settings.
                    restored = MergePrefs(
                        RemoveKeys(existing, ManagedProxyKeys),
                        new Dictionary<string, string>
                        {
                            [NetworkProxyTypeKey] = "5",
                        });
                }

                File.WriteAllText(prefsPath, restored);
                cleared = true;
            }
            catch
            {
                // best-effort per profile
            }
        }

        DeleteBackup();
        _ = relaunch;
        _ = cleared;
    }

    private static bool PrefsPointAtLoopback(string prefsText)
    {
        foreach (var line in prefsText.Split('\n'))
        {
            if (!line.Contains("network.proxy.http", StringComparison.Ordinal) &&
                !line.Contains("network.proxy.ssl", StringComparison.Ordinal))
                continue;
            if (line.Contains("127.0.0.1", StringComparison.Ordinal) ||
                line.Contains("localhost", StringComparison.Ordinal) ||
                line.Contains("[::1]", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumeratePrefsJsPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            yield break;

        string[] roots =
        [
            Path.Combine(home, MozillaDirName, FirefoxDirName),
            Path.Combine(home, "snap", FirefoxDirName, "common", MozillaDirName, FirefoxDirName),
            Path.Combine(home, ".var", "app", "org.mozilla.firefox", MozillaDirName, FirefoxDirName),
            Path.Combine(home, ".var", "app", "org.mozilla.Firefox", MozillaDirName, FirefoxDirName),
        ];

        if (FirefoxCertificateTrust.TryResolveDefaultProfileDirectory(out var defaultProfile, out _) &&
            !string.IsNullOrWhiteSpace(defaultProfile))
        {
            yield return Path.Combine(defaultProfile, "prefs.js");
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var profile in profiles)
                yield return Path.Combine(profile, "prefs.js");
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
            var match = UserPrefLine().Match(line);
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
            var match = UserPrefLine().Match(line);
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

        var snapshotKeys = new HashSet<string>(SnapshotProxyKeys, StringComparer.Ordinal);
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(existingPrefs ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = UserPrefLine().Match(line);
            if (!match.Success)
                continue;
            var key = match.Groups["key"].Value;
            if (snapshotKeys.Contains(key))
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
}

