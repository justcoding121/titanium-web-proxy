using System.Diagnostics;
using System.Runtime.Versioning;

namespace Titanium.Inspector.Services;

/// <summary>
///     Detects Inspector launched from a DMG (/Volumes/…) and copies the .app into
///     Applications so updates and Gatekeeper behave like a normal Mac install.
/// </summary>
internal static class MacAppRelocation
{
    private const string BundleDisplayName = "Titanium Inspector.app";

    /// <summary>True when this process is running from a mounted disk image volume.</summary>
    [SupportedOSPlatform("macos")]
    public static bool IsRunningFromDmgVolume() =>
        IsRunningFromDmgVolume(OperatingSystem.IsMacOS(), TryFindAppBundlePath());

    /// <summary>Same check with the OS and bundle path supplied by the caller (tests).</summary>
    internal static bool IsRunningFromDmgVolume(bool isMacOs, string? bundle)
    {
        if (!isMacOs)
            return false;

        if (bundle is null)
            return false;

        return bundle.StartsWith("/Volumes/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when we should offer Move to Applications (DMG launch, not yet dismissed).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool ShouldOfferMove(InspectorSettings settings) =>
        ShouldOfferMove(settings, OperatingSystem.IsMacOS(), IsRunningFromDmgVolume());

    /// <summary>Same decision with the OS and DMG result supplied by the caller (tests).</summary>
    internal static bool ShouldOfferMove(InspectorSettings settings, bool isMacOs, bool runningFromDmg)
    {
        if (!isMacOs || settings.MacDmgMovePromptDismissed)
            return false;

        return runningFromDmg;
    }

    /// <summary>
    ///     Copies the running .app to Applications (or ~/Applications) and returns the
    ///     destination path. Throws on I/O failure.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string CopyBundleToApplications()
    {
        var source = TryFindAppBundlePath()
            ?? throw new InvalidOperationException("Could not locate Titanium Inspector.app");

        return CopyBundleToApplications(
            source,
            "/Applications",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "/usr/bin/ditto",
            "/usr/bin/xattr");
    }

    /// <summary>
    ///     Copies <paramref name="source"/> into Applications, falling back to the user
    ///     Applications folder when the system folder rejects the copy.
    /// </summary>
    internal static string CopyBundleToApplications(
        string source,
        string applicationsDirectory,
        string userProfile,
        string dittoExecutable,
        string xattrExecutable)
    {
        var systemApps = Path.Combine(applicationsDirectory, BundleDisplayName);
        var userAppsDir = Path.Combine(userProfile, "Applications");
        var userApps = Path.Combine(userAppsDir, BundleDisplayName);

        string dest;
        try
        {
            dest = systemApps;
            CopyBundle(source, dest, dittoExecutable);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Directory.CreateDirectory(userAppsDir);
            dest = userApps;
            CopyBundle(source, dest, dittoExecutable);
        }

        // Clear quarantine on the installed copy (Gatekeeper less surprising for local copies).
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = xattrExecutable,
                ArgumentList = { "-dr", "com.apple.quarantine", dest },
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(5_000);
        }
        catch
        {
            // best-effort
        }

        return dest;
    }

    /// <summary>Opens the installed .app and requests this process exit.</summary>
    [SupportedOSPlatform("macos")]
    public static void RelaunchInstalledApp(string installedAppPath) =>
        RelaunchInstalledApp(installedAppPath, "/usr/bin/open");

    /// <summary>Same relaunch with the <c>open</c> executable supplied by the caller (tests).</summary>
    internal static void RelaunchInstalledApp(string installedAppPath, string openExecutable)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = openExecutable,
            ArgumentList = { installedAppPath },
            UseShellExecute = false,
        });
    }

    /// <summary>Walks up from MacOS/ to the enclosing .app bundle.</summary>
    internal static string? TryFindAppBundlePath() =>
        TryFindAppBundlePath(AppContext.BaseDirectory);

    /// <summary>Walks up from <paramref name="dir"/> to the enclosing .app bundle.</summary>
    internal static string? TryFindAppBundlePath(string? dir)
    {
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(".app/", StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(".app\\", StringComparison.OrdinalIgnoreCase))
            {
                return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            // Contents/MacOS → Contents → .app
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(name, "MacOS", StringComparison.OrdinalIgnoreCase))
            {
                var contents = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                var app = contents is null ? null : Path.GetDirectoryName(contents);
                if (app is not null &&
                    app.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return app;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return null;
    }

    internal static void CopyBundleForTests(string source, string dest, string dittoExecutable) =>
        CopyBundle(source, dest, dittoExecutable);

    private static void CopyBundle(string source, string dest, string dittoExecutable)
    {
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);

        // ditto preserves resource forks / code signature better than recursive File.Copy.
        var psi = new ProcessStartInfo
        {
            FileName = dittoExecutable,
            ArgumentList = { source, dest },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ditto");
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        if (proc.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(err) ? "ditto failed" : err.Trim());
    }
}
