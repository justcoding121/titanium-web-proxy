using System;
using System.IO;
using System.Linq;

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
    private const string PoliciesDirName = "policies";
    private const string ManagedDirName = "managed";

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

        if (UserPolicyDirectories(home).Any(dir => File.Exists(Path.Combine(dir, PolicyFileName))))
            return true;

        // Machine policy under /etc is also ownership: an admin install wrote it.
        // Tests inject roots via <see cref="MachinePolicyRootsForTests"/>.
        return MachinePolicyPaths().Any(File.Exists);
    }

    /// <summary>Test hook: when set, replaces the hard-coded /etc policy roots.</summary>
    internal static string[]? MachinePolicyRootsForTests { get; set; }

    private static string[] UserPolicyDirectories(string home)
    {
        string Dir(params string[] parts) =>
            Path.Combine([home, ConfigDirName, .. parts, PoliciesDirName, ManagedDirName]);

        return
        [
            Dir("google-chrome"),
            Dir("google-chrome-beta"),
            Dir("google-chrome-unstable"),
            Dir("chromium"),
            Dir("BraveSoftware", "Brave-Browser"),
            Dir("microsoft-edge"),
            Dir("opera"),
        ];
    }

    private static string[] MachinePolicyPaths()
    {
        var roots = MachinePolicyRootsForTests ??
        [
            "/etc/opt/chrome",
            "/etc/chromium",
            "/etc/opt/edge",
            "/etc/brave",
            "/etc/opera",
        ];
        return roots.Select(root => Path.Combine(root, PoliciesDirName, ManagedDirName, PolicyFileName)).ToArray();
    }
}
