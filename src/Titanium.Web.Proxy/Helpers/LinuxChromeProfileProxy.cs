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
    private const string ConfigDirName = ".config";
    private const string ProxyKey = "proxy";
    private const string FixedServersMode = "fixed_servers";
    private const string ChromiumDirName = "chromium";
    private const string GoogleChromeDirName = "google-chrome";
    private const string BraveSoftwareDirName = "BraveSoftware";
    private const string BraveBrowserDirName = "Brave-Browser";
    private const string MicrosoftEdgeDirName = "microsoft-edge";
    private const string FlatpakConfigDirName = "config";

    private static readonly object Gate = new();
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static readonly int[] ClearRetryDelaysMs = [300, 800, 1500, 3000];
    private static string? _activeHost;
    private static int _activePort;
    private static bool _activeLoopback;
    private static CancellationTokenSource? _clearRetryCts;

    /// <summary>
    ///     Writes fixed proxy into Chromium-family profile Preferences; returns profiles updated.
    ///     <paramref name="proxyLoopback"/> keeps localhost on the proxy (the Inspector "send localhost" option).
    /// </summary>
    internal static int Apply(string hostname, int port, bool proxyLoopback = false)
    {
        lock (Gate)
        {
            CancelClearRetries_NoLock();
            LinuxProxyFailOpen.Stop();
            _activeHost = hostname;
            _activePort = port;
            _activeLoopback = proxyLoopback;
            WriteMarker(hostname, port);
            EnsureInstalledDefaultProfiles();

            var written = EnumeratePreferencesPaths().Count(prefsPath => TryWritePreferences(prefsPath, hostname, port));

            RestartWatchers_NoLock();
            return written;
        }
    }

    internal static void Clear() => Clear(startFailOpen: true);

    /// <param name="startFailOpen">
    ///     When true (graceful quit while browsers are still open), bind a short fail-open
    ///     helper so Chromium is not left dialing a closed port. Dead-app ForceClear passes
    ///     false and stops any leftover helper instead.
    /// </param>
    internal static void Clear(bool startFailOpen)
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
                // The marker file is gone after a kill. A loopback fixed_servers
                // entry still ignores the desktop proxy switch, so drop it too.
                TryStripLoopbackFixedServer(prefsPath);
            }

            DeleteMarker();
            if (startFailOpen)
                ScheduleClearRetries_NoLock(host, port);
            else
                CancelClearRetries_NoLock();
        }

        if (!startFailOpen)
        {
            LinuxProxyFailOpen.Stop();
            return;
        }

        // Outside the lock. Launch now, detached. A delayed task dies with File → Exit
        // before it binds, and Edge is left dialing a closed port. The helper retries
        // the bind until this process releases the listener.
        if (!string.IsNullOrEmpty(host) && port > 0)
            LinuxProxyFailOpen.Start(host, port);
    }

    /// <summary>
    ///     Create a Chromium user-data directory for each installed browser that does not
    ///     have one yet, and mark first-run complete. A missing profile makes the browser
    ///     sit on the welcome flow and never open the requested page, so the proxy sees
    ///     no session. The directory is then picked up by <see cref="EnumeratePreferencesPaths"/>.
    /// </summary>
    private static void EnsureInstalledDefaultProfiles()
    {
        if (!OperatingSystem.IsLinux())
            return;
        EnsureInstalledDefaultProfiles(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Creates default profiles for the usual browser install markers under <paramref name="home"/>.</summary>
    internal static void EnsureInstalledDefaultProfiles(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return;

        // Marker file that exists only when that browser is installed, and the config
        // directory under ~/.config.
        var braveRelative = Path.Combine(BraveSoftwareDirName, BraveBrowserDirName);
        (string Marker, string Relative)[] installed =
        [
            ("/usr/bin/google-chrome-stable", GoogleChromeDirName),
            ("/usr/bin/google-chrome", GoogleChromeDirName),
            ("/usr/share/applications/google-chrome.desktop", GoogleChromeDirName),
            ("/usr/bin/chromium", ChromiumDirName),
            ("/usr/bin/chromium-browser", ChromiumDirName),
            ("/usr/bin/brave-browser", braveRelative),
            ("/usr/share/applications/brave-browser.desktop", braveRelative),
            ("/usr/bin/microsoft-edge-stable", MicrosoftEdgeDirName),
            ("/usr/bin/microsoft-edge", MicrosoftEdgeDirName),
            ("/usr/bin/opera", "opera"),
            ("/usr/share/applications/opera.desktop", "opera"),
        ];
        EnsureInstalledProfiles(home, installed);
    }

    internal static void EnsureInstalledProfilesForTests(
        string home, (string Marker, string Relative)[] installed) =>
        EnsureInstalledProfiles(home, installed);

    private static void EnsureInstalledProfiles(
        string home, (string Marker, string Relative)[] installed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (marker, relative) in installed)
        {
            if (!File.Exists(marker))
                continue;
            var root = Path.Combine(home, ConfigDirName, relative);
            if (!seen.Add(root))
                continue;
            EnsureDefaultProfile(root);
        }
    }

    /// <summary>Creates <c>Default/</c>, the first-run sentinel, and a minimal Local State.</summary>
    internal static void EnsureDefaultProfile(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Default"));
        var sentinel = Path.Combine(root, "First Run");
        if (!File.Exists(sentinel))
            File.WriteAllText(sentinel, string.Empty);
        var localState = Path.Combine(root, "Local State");
        if (!File.Exists(localState))
        {
            File.WriteAllText(localState,
                "{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Person 1\"}}}}");
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
        yield return Path.Combine(home, ConfigDirName, GoogleChromeDirName);
        yield return Path.Combine(home, ConfigDirName, GoogleChromeDirName + "-beta");
        yield return Path.Combine(home, ConfigDirName, GoogleChromeDirName + "-unstable");
        yield return Path.Combine(home, ConfigDirName, ChromiumDirName);
        yield return Path.Combine(home, ConfigDirName, BraveSoftwareDirName, BraveBrowserDirName);
        yield return Path.Combine(home, ConfigDirName, MicrosoftEdgeDirName);
        yield return Path.Combine(home, ConfigDirName, "opera");
        yield return Path.Combine(home, "snap", ChromiumDirName, "common", ChromiumDirName);
        yield return Path.Combine(home, "snap", MicrosoftEdgeDirName, "common", MicrosoftEdgeDirName);
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", FlatpakConfigDirName, GoogleChromeDirName);
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", FlatpakConfigDirName, ChromiumDirName);
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", FlatpakConfigDirName, BraveSoftwareDirName,
            BraveBrowserDirName);
        yield return Path.Combine(home, ".var", "app", "com.microsoft.Edge", FlatpakConfigDirName, MicrosoftEdgeDirName);
    }

    // Test hooks
    internal static bool TryApplyToFileForTests(string prefsPath, string hostname, int port) =>
        TryWritePreferences(prefsPath, hostname, port);

    internal static void TryRestoreFileForTests(string prefsPath) =>
        TryRestorePreferences(prefsPath);

    internal static bool TryStripInspectorProxyForTests(string prefsPath, string hostname, int port) =>
        TryStripInspectorProxy(prefsPath, hostname, port);

    internal static bool TryStripLoopbackFixedServerForTests(string prefsPath) =>
        TryStripLoopbackFixedServer(prefsPath);

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
                var original = root[ProxyKey]?.ToJsonString() ?? "null";
                File.WriteAllText(backupPath, original);
            }

            root[ProxyKey] = BuildFixedServersProxy(hostname, port);

            AtomicWriteJson(prefsPath, root);

            var verify = File.ReadAllText(prefsPath);
            return verify.Contains(FixedServersMode, StringComparison.Ordinal) &&
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
                root.Remove(ProxyKey);
            else
                root[ProxyKey] = JsonNode.Parse(original);

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
                !text.Contains(FixedServersMode, StringComparison.Ordinal))
                return true;

            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                       ?? new JsonObject();
            var proxy = root[ProxyKey] as JsonObject;
            if (proxy is null)
                return true;

            var server = proxy["server"]?.GetValue<string>() ?? string.Empty;
            var mode = proxy["mode"]?.GetValue<string>() ?? string.Empty;
            var pointsAtUs = server.Contains($"{hostname}:{port}", StringComparison.OrdinalIgnoreCase) ||
                             (mode.Equals(FixedServersMode, StringComparison.OrdinalIgnoreCase) &&
                              server.Contains(hostname, StringComparison.OrdinalIgnoreCase) &&
                              server.Contains(port.ToString(), StringComparison.Ordinal));

            if (!pointsAtUs && !mode.Equals(FixedServersMode, StringComparison.OrdinalIgnoreCase))
                return true;

            // Prefer OS/system proxy (gsettings already restored) over a dead fixed endpoint.
            root[ProxyKey] = new JsonObject { ["mode"] = "system" };
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

    /// <summary>
    ///     Drop <c>fixed_servers</c> when the server is loopback, even if the marker file
    ///     was already deleted. A proxy the user set to any other host is left alone.
    /// </summary>
    private static bool TryStripLoopbackFixedServer(string prefsPath)
    {
        try
        {
            if (!File.Exists(prefsPath))
                return false;

            var text = File.ReadAllText(prefsPath);
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                       ?? new JsonObject();
            var proxy = root[ProxyKey] as JsonObject;
            if (proxy is null)
                return false;

            var mode = proxy["mode"]?.GetValue<string>() ?? string.Empty;
            var server = proxy["server"]?.GetValue<string>() ?? string.Empty;
            if (!mode.Equals(FixedServersMode, StringComparison.OrdinalIgnoreCase) ||
                !IsLoopbackProxyServer(server))
                return false;

            root[ProxyKey] = new JsonObject { ["mode"] = "system" };
            AtomicWriteJson(prefsPath, root);
            var backupPath = prefsPath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* ignore */ }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopbackProxyServer(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri))
            return false;
        return uri.Host is "127.0.0.1" or "localhost" or "::1";
    }

    private static JsonObject BuildFixedServersProxy(string hostname, int port)
    {
        var proxy = new JsonObject
        {
            ["mode"] = FixedServersMode,
            ["server"] = $"http://{hostname}:{port}", // NOSONAR S5332 -- Chromium proxy.mode fixed_servers requires an http proxy URL.
        };
        if (_activeLoopback)
            proxy["bypass_list"] = LinuxBrowserLaunchProxy.ProxyBypassList;
        return proxy;
    }

    private static void AtomicWriteJson(string prefsPath, JsonObject root)
    {
        var tmp = prefsPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Move(tmp, prefsPath, overwrite: true);
    }

    private static void ScheduleClearRetries_NoLock(string? host, int port)
    {
        CancelClearRetries_NoLock();
        // Still retry when the marker is gone: Chrome can flush loopback fixed_servers
        // after the first strip.

        var cts = new CancellationTokenSource();
        _clearRetryCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            // Chrome often flushes Preferences shortly after Inspector restores them.
            foreach (var delayMs in ClearRetryDelaysMs)
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
                    {
                        if (!string.IsNullOrEmpty(host) && port > 0)
                            TryStripInspectorProxy(prefsPath, host, port);
                        TryStripLoopbackFixedServer(prefsPath);
                    }
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
        return Path.Combine(home, ConfigDirName, "TitaniumInspector", MarkerFileName);
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
                await Task.Delay(250, _clearRetryCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                ReassertIfNeeded(e.FullPath);
            }
            catch
            {
                // ignore
            }
        }, _clearRetryCts?.Token ?? CancellationToken.None);
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
            if (text.Contains(FixedServersMode, StringComparison.Ordinal) &&
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
