using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Persist Chromium <c>Preferences</c> <c>proxy.mode=fixed_servers</c> so launches that bypass
///     .desktop / XFCE helpers (plain <c>/usr/bin/google-chrome-stable</c>) still route through
///     Inspector. User-level managed policy JSON is often ignored by Google Chrome on Linux;
///     profile Preferences are read on browser start.
/// </summary>
internal static class LinuxChromeProfileProxy
{
    internal const string BackupSuffix = ".titanium-inspector-proxy.bak";
    private static readonly object Gate = new();
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static string? _activeHost;
    private static int _activePort;

    /// <summary>Writes fixed proxy into Chromium-family profile Preferences; returns profiles updated.</summary>
    internal static int Apply(string hostname, int port)
    {
        lock (Gate)
        {
            _activeHost = hostname;
            _activePort = port;
            var written = 0;
            foreach (var prefsPath in EnumeratePreferencesPaths())
            {
                if (TryWritePreferences(prefsPath, hostname, port))
                    written++;
            }

            RestartWatchers();
            return written;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            StopWatchers();
            _activeHost = null;
            _activePort = 0;
            foreach (var prefsPath in EnumeratePreferencesPaths())
                TryRestorePreferences(prefsPath);
        }
    }

    internal static IEnumerable<string> EnumeratePreferencesPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            yield break;

        foreach (var root in BrowserConfigRoots(home))
        {
            if (!Directory.Exists(root))
                continue;

            // Default + Profile N
            var defaultPrefs = Path.Combine(root, "Default", "Preferences");
            if (File.Exists(defaultPrefs) || Directory.Exists(Path.Combine(root, "Default")))
                yield return defaultPrefs;

            string[] profiles;
            try
            {
                profiles = Directory.GetDirectories(root, "Profile *");
            }
            catch
            {
                continue;
            }

            foreach (var profileDir in profiles)
                yield return Path.Combine(profileDir, "Preferences");
        }
    }

    private static IEnumerable<string> BrowserConfigRoots(string home)
    {
        yield return Path.Combine(home, ".config", "google-chrome");
        yield return Path.Combine(home, ".config", "google-chrome-beta");
        yield return Path.Combine(home, ".config", "google-chrome-unstable");
        yield return Path.Combine(home, ".config", "chromium");
        yield return Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser");
        yield return Path.Combine(home, ".config", "microsoft-edge");
        yield return Path.Combine(home, "snap", "chromium", "common", "chromium");
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", "config", "google-chrome");
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", "config", "chromium");
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", "config", "BraveSoftware",
            "Brave-Browser");
    }

    // Test hooks
    internal static bool TryApplyToFileForTests(string prefsPath, string hostname, int port) =>
        TryWritePreferences(prefsPath, hostname, port);

    internal static void TryRestoreFileForTests(string prefsPath) =>
        TryRestorePreferences(prefsPath);

    private static bool TryWritePreferences(string prefsPath, string hostname, int port)
    {
        try
        {
            var dir = Path.GetDirectoryName(prefsPath);
            if (string.IsNullOrEmpty(dir))
                return false;
            Directory.CreateDirectory(dir);

            JsonObject root;
            if (File.Exists(prefsPath))
            {
                var text = File.ReadAllText(prefsPath);
                root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                       ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var backupPath = prefsPath + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                // Preserve original proxy node (or explicit null) so Clear can restore.
                var original = root["proxy"]?.ToJsonString() ?? "null";
                File.WriteAllText(backupPath, original);
            }

            root["proxy"] = new JsonObject
            {
                ["mode"] = "fixed_servers",
                ["server"] = $"http://{hostname}:{port}",
                ["bypass_list"] = LinuxBrowserLaunchProxy.ProxyBypassList,
            };

            var tmp = prefsPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, prefsPath, overwrite: true);

            // Verify
            var verify = File.ReadAllText(prefsPath);
            return verify.Contains("fixed_servers", StringComparison.Ordinal) &&
                   verify.Contains($"{hostname}:{port}", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryRestorePreferences(string prefsPath)
    {
        try
        {
            var backupPath = prefsPath + BackupSuffix;
            if (!File.Exists(backupPath))
                return;

            var original = File.ReadAllText(backupPath).Trim();
            if (!File.Exists(prefsPath))
            {
                File.Delete(backupPath);
                return;
            }

            var text = File.ReadAllText(prefsPath);
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                       ?? new JsonObject();
            if (original == "null" || string.IsNullOrWhiteSpace(original))
                root.Remove("proxy");
            else
                root["proxy"] = JsonNode.Parse(original);

            var tmp = prefsPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, prefsPath, overwrite: true);
            File.Delete(backupPath);
        }
        catch
        {
            // best-effort
        }
    }

    private static void RestartWatchers()
    {
        StopWatchers();
        if (string.IsNullOrEmpty(_activeHost) || _activePort <= 0)
            return;

        foreach (var prefsPath in EnumeratePreferencesPaths().Where(File.Exists).Distinct())
        {
            try
            {
                var dir = Path.GetDirectoryName(prefsPath)!;
                var name = Path.GetFileName(prefsPath);
                var watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnPreferencesChanged;
                watcher.Created += OnPreferencesChanged;
                Watchers.Add(watcher);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void StopWatchers()
    {
        foreach (var watcher in Watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnPreferencesChanged;
                watcher.Created -= OnPreferencesChanged;
                watcher.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        Watchers.Clear();
    }

    private static void OnPreferencesChanged(object sender, FileSystemEventArgs e)
    {
        // Chrome often rewrites Preferences on exit with in-memory DIRECT/system settings.
        // Debounce Chrome's multi-write Preferences flush, then re-assert while proxy is enabled.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(250).ConfigureAwait(false);
                ReassertIfNeeded(e.FullPath);
            }
            catch
            {
                // ignore
            }
        });
    }

    private static void ReassertIfNeeded(string fullPath)
    {
        string? host;
        int port;
        lock (Gate)
        {
            host = _activeHost;
            port = _activePort;
        }

        if (string.IsNullOrEmpty(host) || port <= 0)
            return;

        try
        {
            if (!File.Exists(fullPath))
                return;
            var text = File.ReadAllText(fullPath);
            if (text.Contains("fixed_servers", StringComparison.Ordinal) &&
                text.Contains($"{host}:{port}", StringComparison.Ordinal))
                return;

            lock (Gate)
            {
                if (_activeHost != host || _activePort != port)
                    return;
                TryWritePreferences(fullPath, host, port);
            }
        }
        catch
        {
            // ignore
        }
    }
}
