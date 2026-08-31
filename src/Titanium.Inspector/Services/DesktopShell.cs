using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Titanium.Inspector.Services;

/// <summary>
/// Cross-platform open-in-file-manager helpers. Command builders are pure for unit tests;
/// <see cref="TryOpenDirectory"/> / <see cref="TryRevealFileOrOpenDirectory"/> invoke the OS.
/// </summary>
public static class DesktopShell
{
    public readonly record struct ShellCommand(string FileName, string Arguments);

    public static bool TryBuildOpenDirectory(
        string? directory,
        OSPlatform platform,
        out ShellCommand command,
        out string? error)
    {
        command = default;
        error = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "Directory path is empty.";
            return false;
        }

        var full = Path.GetFullPath(directory.Trim());
        if (platform == OSPlatform.Windows)
        {
            command = new ShellCommand("explorer.exe", QuoteWindowsArg(full));
            return true;
        }

        if (platform == OSPlatform.OSX)
        {
            command = new ShellCommand("open", QuoteUnixArg(full));
            return true;
        }

        if (platform == OSPlatform.Linux)
        {
            command = new ShellCommand("xdg-open", QuoteUnixArg(full));
            return true;
        }

        error = "Unsupported operating system.";
        return false;
    }

    public static bool TryBuildRevealFile(
        string? filePath,
        OSPlatform platform,
        out ShellCommand command,
        out string? error)
    {
        command = default;
        error = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "File path is empty.";
            return false;
        }

        var full = Path.GetFullPath(filePath.Trim());
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            error = "File path has no directory.";
            return false;
        }

        if (platform == OSPlatform.Windows)
        {
            // explorer /select,"C:\path\file.log"
            command = new ShellCommand("explorer.exe", "/select," + QuoteWindowsArg(full));
            return true;
        }

        if (platform == OSPlatform.OSX)
        {
            command = new ShellCommand("open", "-R " + QuoteUnixArg(full));
            return true;
        }

        if (platform == OSPlatform.Linux)
        {
            // No portable reveal; open the containing folder.
            return TryBuildOpenDirectory(directory, platform, out command, out error);
        }

        error = "Unsupported operating system.";
        return false;
    }

    public static bool TryOpenDirectory(string? directory, out string? error) =>
        TryOpenDirectory(directory, GetCurrentPlatform(), out error);

    public static bool TryOpenDirectory(string? directory, OSPlatform platform, out string? error)
    {
        if (!TryBuildOpenDirectory(directory, platform, out var command, out error))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetFullPath(directory!.Trim()));
            Start(command);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryRevealFileOrOpenDirectory(string? filePath, out string? error) =>
        TryRevealFileOrOpenDirectory(filePath, GetCurrentPlatform(), out error);

    public static bool TryRevealFileOrOpenDirectory(string? filePath, OSPlatform platform, out string? error)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "File path is empty.";
            return false;
        }

        var full = Path.GetFullPath(filePath.Trim());
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            error = "File path has no directory.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            if (!TryBuildRevealFile(full, platform, out var command, out error))
            {
                return false;
            }

            Start(command);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static OSPlatform GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OSPlatform.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return OSPlatform.OSX;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return OSPlatform.Linux;
        }

        return OSPlatform.Create("Unknown");
    }

    private static void Start(ShellCommand command)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
        });
    }

    private static string QuoteWindowsArg(string path) =>
        "\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteUnixArg(string path) =>
        "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
