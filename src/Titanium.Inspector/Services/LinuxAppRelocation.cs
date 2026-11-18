using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Inspector.Services;

/// <summary>
///     Outcome when an AppImage-hosted Inspector meets a local user install.
/// </summary>
internal enum LinuxRelocateAction
{
    Install,
    Replace,
    OpenExisting,
}

/// <summary>
///     Detects Inspector launched from an AppImage and installs into
///     ~/.local/share/TitaniumInspector (same layout as packaging install.sh).
/// </summary>
internal static class LinuxAppRelocation
{
    internal const string AppDirName = "TitaniumInspector";
    internal const string ExeName = "TitaniumInspector";

    /// <summary>True when APPIMAGE is set or BaseDirectory looks like a squashfs mount.</summary>
    [SupportedOSPlatform("linux")]
    public static bool IsRunningFromAppImage()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
            return true;

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // appimagetool mounts under /tmp/.mount_* or /tmp/appimage_*
        return baseDir.Contains("/.mount_", StringComparison.Ordinal)
               || baseDir.Contains("/appimage_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Offer Install locally when running from AppImage (or re-offer when newer than install).
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool ShouldOfferInstall(InspectorSettings settings)
    {
        if (!OperatingSystem.IsLinux() || !IsRunningFromAppImage())
            return false;

        if (!settings.LinuxAppImageInstallPromptDismissed)
            return true;

        var decision = DecideRelocateAction();
        return decision is LinuxRelocateAction.Install or LinuxRelocateAction.Replace;
    }

    [SupportedOSPlatform("linux")]
    public static LinuxRelocateAction DecideRelocateAction(
        Version? runningVersion = null,
        Version? installedVersion = null,
        string? installDir = null)
    {
        runningVersion ??= ReadRunningVersion();

        if (installedVersion is not null)
        {
            return ReleaseVersion.Compare(runningVersion, installedVersion) > 0
                ? LinuxRelocateAction.Replace
                : LinuxRelocateAction.OpenExisting;
        }

        installDir ??= DefaultInstallDir();
        var exe = Path.Combine(installDir, ExeName);
        if (!File.Exists(exe))
            return LinuxRelocateAction.Install;

        var fromExe = ReadInstalledVersion(exe);
        if (fromExe is null)
            return LinuxRelocateAction.OpenExisting;

        return ReleaseVersion.Compare(runningVersion, fromExe) > 0
            ? LinuxRelocateAction.Replace
            : LinuxRelocateAction.OpenExisting;
    }

    [SupportedOSPlatform("linux")]
    public static string DefaultInstallDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", AppDirName);
    }

    [SupportedOSPlatform("linux")]
    public static string DefaultWrapperPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "bin", "titanium-inspector");
    }

    /// <summary>
    ///     Copy current AppImage payload into ~/.local/share/TitaniumInspector and register a wrapper.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static string InstallToLocalShare()
    {
        var sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dest = DefaultInstallDir();
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);

        Directory.CreateDirectory(dest);
        CopyDirectory(sourceDir, dest);

        var exe = Path.Combine(dest, ExeName);
        if (File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    ArgumentList = { "+x", exe },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(5_000);
            }
            catch
            {
                // best-effort
            }
        }

        TryWriteWrapper(dest);
        TryWriteDesktopEntry(dest);
        return dest;
    }

    [SupportedOSPlatform("linux")]
    public static void RelaunchInstalled(string installDir)
    {
        var exe = Path.Combine(installDir, ExeName);
        if (!File.Exists(exe))
            throw new FileNotFoundException("Installed TitaniumInspector not found", exe);

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = installDir,
            UseShellExecute = false,
        });
    }

    internal static Version ReadRunningVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return ReleaseVersion.ToComparable(asm.GetName().Version);
    }

    /// <summary>
    ///     Best-effort: read FileVersionInfo from the installed binary.
    ///     The Linux apphost often has no version resource; the sibling dll does.
    /// </summary>
    internal static Version? ReadInstalledVersion(string exePath)
    {
        var fromFile = TryReadFileVersion(exePath);
        if (fromFile is not null)
            return fromFile;

        var dll = Path.ChangeExtension(exePath, ".dll");
        if (!string.Equals(dll, exePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(dll))
            return TryReadFileVersion(dll);

        return null;
    }

    private static Version? TryReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)
                && Version.TryParse(ReleaseVersion.StripPrerelease(info.ProductVersion.Split('+')[0]), out var product))
                return ReleaseVersion.ToComparable(product);

            if (!string.IsNullOrWhiteSpace(info.FileVersion)
                && Version.TryParse(ReleaseVersion.StripPrerelease(info.FileVersion), out var file))
                return ReleaseVersion.ToComparable(file);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void TryWriteWrapper(string installDir)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var wrapper = Path.Combine(home, ".local", "bin", "titanium-inspector");
            Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
            var exe = Path.Combine(installDir, ExeName);
            File.WriteAllText(wrapper, "#!/usr/bin/env bash\nexec \"" + exe.Replace("\"", "\\\"", StringComparison.Ordinal) + "\" \"$@\"\n");
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                ArgumentList = { "+x", wrapper },
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(5_000);
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryWriteDesktopEntry(string installDir)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var apps = Path.Combine(home, ".local", "share", "applications");
            Directory.CreateDirectory(apps);
            var exe = Path.Combine(installDir, ExeName);
            var desktop = Path.Combine(apps, "titanium-inspector.desktop");
            File.WriteAllText(desktop,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Titanium Inspector\n" +
                "Exec=\"" + exe.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"\n" +
                "Terminal=false\n" +
                "Categories=Network;Development;\n");
        }
        catch
        {
            // best-effort
        }
    }
}
