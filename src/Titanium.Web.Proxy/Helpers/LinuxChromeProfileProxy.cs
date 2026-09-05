using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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
    private const string MarkerFileName = "chrome-profile-proxy.json";

    private static readonly object Gate = new();
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static string? _activeHost;
    private static int _activePort;
    private static CancellationTokenSource? _clearRetryCts;

    /// <summary>Writes fixed proxy into Chromium-family profile Preferences; returns profiles updated.</summary>
    internal static int Apply(string hostname, int port)
    {
        lock (Gate)
        {
            CancelClearRetries_NoLock();
            LinuxProxyFailOpen.Stop();
            _activeHost = hostname;
            _activePort = port;
            WriteMarker(hostname, port);

            var written = 0;
            foreach (var prefsPath in EnumeratePreferencesPaths())
            {
                if (TryWritePreferences(prefsPath, hostname, port))
                    written++;
            }

            RestartWatchers_NoLock();
            return written;
        }
    }

    internal static void Clear()
    {
        string? host;
        int port;
        lock (Gate)
        {
            host = _activeHost;
            port = _activePort;
            if ((string.IsNullOrEmpty(host) || port <= 0) &&
                TryReadMarker(out var markerHost, out var markerPort))
            {
                host = markerHost;
                port = markerPort;
            }

            StopWatchers_NoLock();
            _activeHost = null;
            _activePort = 0;

            foreach (var prefsPath in EnumeratePreferencesPaths())
            {
                TryRestorePreferences(prefsPath);
                if (!string.IsNullOrEmpty(host) && port > 0)
                    TryStripInspectorProxy(prefsPath, host, port);
            }

            DeleteMarker();
            ScheduleClearRetries_NoLock(host, port);
        }

        // Outside the lock: ProxyServer may still hold the port for a moment during Stop().
        if (!string.IsNullOrEmpty(host) && port > 0)
        {
            var h = host;
            var p = port;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    LinuxProxyFailOpen.Start(h, p);
                }
                catch
                {
                    // ignore
                }
            });
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
        yield return Path.Combine(home, "snap", "microsoft-edge", "common", "microsoft-edge");
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", "config", "google-chrome");
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", "config", "chromium");
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", "config", "BraveSoftware",
            "Brave-Browser");
        yield return Path.Combine(home, ".var", "app", "com.microsoft.Edge", "config", "microsoft-edge");
    }

    // Test hooks
    internal static bool TryApplyToFileForTests(string prefsPath, string hostname, int port) =>
        TryWritePreferences(prefsPath, hostname, port);

    internal static void TryRestoreFileForTests(string prefsPath) =>
        TryRestorePreferences(prefsPath);

    internal static bool TryStripInspectorProxyForTests(string prefsPath, string hostname, int port) =>
        TryStripInspectorProxy(prefsPath, hostname, port);

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
                var original = root["proxy"]?.ToJsonString() ?? "null";
                File.WriteAllText(backupPath, original);
            }

            root["proxy"] = BuildFixedServersProxy(hostname, port);

            AtomicWriteJson(prefsPath, root);

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

            AtomicWriteJson(prefsPath, root);
            File.Delete(backupPath);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    ///     Force Preferences away from Inspector's fixed proxy. Used after restore and on retries so a
    ///     still-running Chrome that flushes in-memory fixed_servers cannot leave a dead proxy on disk.
    /// </summary>
    private static bool TryStripInspectorProxy(string prefsPath, string hostname, int port)
    {
        try
        {
            if (!File.Exists(prefsPath))
                return false;

            var text = File.ReadAllText(prefsPath);
            if (!text.Contains($"{hostname}:{port}", StringComparison.Ordinal) &&
                !text.Contains("fixed_servers", StringComparison.Ordinal))
                return true;

            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                       ?? new JsonObject();
            var proxy = root["proxy"] as JsonObject;
            if (proxy is null)
                return true;

            var server = proxy["server"]?.GetValue<string>() ?? string.Empty;
            var mode = proxy["mode"]?.GetValue<string>() ?? string.Empty;
            var pointsAtUs = server.Contains($"{hostname}:{port}", StringComparison.OrdinalIgnoreCase) ||
                             (mode.Equals("fixed_servers", StringComparison.OrdinalIgnoreCase) &&
                              server.Contains(hostname, StringComparison.OrdinalIgnoreCase) &&
                              server.Contains(port.ToString(), StringComparison.Ordinal));

            if (!pointsAtUs && !mode.Equals("fixed_servers", StringComparison.OrdinalIgnoreCase))
                return true;

            // Prefer OS/system proxy (gsettings already restored) over a dead fixed endpoint.
            root["proxy"] = new JsonObject { ["mode"] = "system" };
            AtomicWriteJson(prefsPath, root);

            // Drop backup if strip replaced our endpoint — original restore already attempted.
            var backupPath = prefsPath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* ignore */ }
            }

            var verify = File.ReadAllText(prefsPath);
            return !verify.Contains($"{hostname}:{port}", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject BuildFixedServersProxy(string hostname, int port) =>
        new()
        {
            ["mode"] = "fixed_servers",
            ["server"] = $"http://{hostname}:{port}",
            ["bypass_list"] = LinuxBrowserLaunchProxy.ProxyBypassList,
        };

    private static void AtomicWriteJson(string prefsPath, JsonObject root)
    {
        var tmp = prefsPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Move(tmp, prefsPath, overwrite: true);
    }

    private static void ScheduleClearRetries_NoLock(string? host, int port)
    {
        CancelClearRetries_NoLock();
        if (string.IsNullOrEmpty(host) || port <= 0)
            return;

        var cts = new CancellationTokenSource();
        _clearRetryCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            // Chrome often flushes Preferences shortly after Inspector restores them.
            foreach (var delayMs in new[] { 300, 800, 1500, 3000 })
            {
                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (Gate)
                {
                    if (token.IsCancellationRequested || _activeHost is not null)
                        return;
                    foreach (var prefsPath in EnumeratePreferencesPaths())
                        TryStripInspectorProxy(prefsPath, host, port);
                }
            }
        }, token);
    }

    private static void CancelClearRetries_NoLock()
    {
        try
        {
            _clearRetryCts?.Cancel();
            _clearRetryCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _clearRetryCts = null;
    }

    private static string? MarkerPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        return Path.Combine(home, ".config", "TitaniumInspector", MarkerFileName);
    }

    private static void WriteMarker(string hostname, int port)
    {
        try
        {
            var path = MarkerPath();
            if (path is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(new { hostname, port }));
        }
        catch
        {
            // ignore
        }
    }

    private static bool TryReadMarker(out string hostname, out int port)
    {
        hostname = string.Empty;
        port = 0;
        try
        {
            var path = MarkerPath();
            if (path is null || !File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("hostname", out var h) ||
                !doc.RootElement.TryGetProperty("port", out var p))
                return false;
            hostname = h.GetString() ?? string.Empty;
            port = p.GetInt32();
            return !string.IsNullOrWhiteSpace(hostname) && port > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteMarker()
    {
        try
        {
            var path = MarkerPath();
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static void RestartWatchers_NoLock()
    {
        StopWatchers_NoLock();
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

    private static void StopWatchers_NoLock()
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
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);
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
