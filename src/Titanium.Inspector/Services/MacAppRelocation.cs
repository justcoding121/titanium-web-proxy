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
    public static bool IsRunningFromDmgVolume()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var bundle = TryFindAppBundlePath();
        if (bundle is null)
            return false;

        return bundle.StartsWith("/Volumes/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when we should offer Move to Applications (DMG launch, not yet dismissed).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool ShouldOfferMove(InspectorSettings settings)
    {
        if (!OperatingSystem.IsMacOS() || settings.MacDmgMovePromptDismissed)
            return false;

        return IsRunningFromDmgVolume();
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

        var systemApps = Path.Combine("/Applications", BundleDisplayName);
        var userAppsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications");
        var userApps = Path.Combine(userAppsDir, BundleDisplayName);

        string dest;
        try
        {
            dest = systemApps;
            CopyBundle(source, dest);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Directory.CreateDirectory(userAppsDir);
            dest = userApps;
            CopyBundle(source, dest);
        }

        // Clear quarantine on the installed copy (Gatekeeper less surprising for local copies).
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/xattr",
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
    public static void RelaunchInstalledApp(string installedAppPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            ArgumentList = { installedAppPath },
            UseShellExecute = false,
        });
    }

    /// <summary>Walks up from MacOS/ to the enclosing .app bundle.</summary>
    internal static string? TryFindAppBundlePath()
    {
        var dir = AppContext.BaseDirectory;
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

    private static void CopyBundle(string source, string dest)
    {
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);

        // ditto preserves resource forks / code signature better than recursive File.Copy.
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/ditto",
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
