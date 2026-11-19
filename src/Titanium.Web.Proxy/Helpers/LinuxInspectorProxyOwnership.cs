using System;
using System.IO;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Evidence that Inspector (not some other local forwarder) last owned the desktop
///     loopback proxy. The login/timer clear helper must see at least one of these before
///     it touches GNOME/KDE or browser hooks; otherwise a developer's own 127.0.0.1 proxy
///     would be wiped every five minutes after installing the deb.
/// </summary>
internal static class LinuxInspectorProxyOwnership
{
    private const string ConfigDirName = ".config";
    private const string InspectorDirName = "TitaniumInspector";
    private const string ProxySnapshotFileName = "proxy-snapshot.json";
    private const string ChromeMarkerFileName = "chrome-profile-proxy.json";
    private const string FailOpenPidFileName = "fail-open-proxy.pid";
    private const string FirefoxBackupFileName = "firefox-proxy-backup.json";
    private const string PolicyFileName = "titanium-inspector-proxy.json";

    /// <summary>
    ///     True when any on-disk marker proves Inspector configured the proxy for this user.
    ///     A clean quit deletes the snapshot; a crash or kill leaves evidence so the timer
    ///     can still recover. <paramref name="homeOverride"/> is for unit tests.
    /// </summary>
    internal static bool HasEvidence(string? homeOverride = null)
    {
        var home = homeOverride;
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return false;

        var inspectorDir = Path.Combine(home, ConfigDirName, InspectorDirName);
        if (File.Exists(Path.Combine(inspectorDir, ProxySnapshotFileName))
            || File.Exists(Path.Combine(inspectorDir, ChromeMarkerFileName))
            || File.Exists(Path.Combine(inspectorDir, FailOpenPidFileName))
            || File.Exists(Path.Combine(inspectorDir, FirefoxBackupFileName)))
            return true;

        foreach (var dir in UserPolicyDirectories(home))
        {
            if (File.Exists(Path.Combine(dir, PolicyFileName)))
                return true;
        }

        // Machine policy under /etc is also ownership: an admin install wrote it.
        foreach (var path in MachinePolicyPaths())
        {
            if (File.Exists(path))
                return true;
        }

        return false;
    }

    private static string[] UserPolicyDirectories(string home) =>
    [
        Path.Combine(home, ConfigDirName, "google-chrome", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "google-chrome-beta", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "google-chrome-unstable", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "chromium", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "BraveSoftware", "Brave-Browser", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "microsoft-edge", "policies", "managed"),
        Path.Combine(home, ConfigDirName, "opera", "policies", "managed"),
    ];

    private static string[] MachinePolicyPaths() =>
    [
        Path.Combine("/etc/opt/chrome", "policies", "managed", PolicyFileName),
        Path.Combine("/etc/chromium", "policies", "managed", PolicyFileName),
        Path.Combine("/etc/opt/edge", "policies", "managed", PolicyFileName),
        Path.Combine("/etc/brave", "policies", "managed", PolicyFileName),
        Path.Combine("/etc/opera", "policies", "managed", PolicyFileName),
    ];
}
