using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Titanium.Inspector.DesktopProbe.Shared;

/// <summary>Locate Chrome / Edge / Firefox without requiring --proxy-server.</summary>
public static class BrowserPaths
{
    public static string? FindChrome()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FirstExisting("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");

        return Which("google-chrome") ?? Which("google-chrome-stable") ?? Which("chromium") ?? Which("chromium-browser");
    }

    public static string? FindEdge()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FirstExisting("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");

        return Which("microsoft-edge") ?? Which("microsoft-edge-stable");
    }

    public static string? FindFirefox()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FirstExisting("/Applications/Firefox.app/Contents/MacOS/firefox");

        return Which("firefox");
    }

    public static IEnumerable<(string Name, string Path)> ResolveAuto()
    {
        if (FindEdge() is { } edge)
            yield return ("edge", edge);
        if (FindChrome() is { } chrome)
            yield return ("chrome", chrome);
        if (FindFirefox() is { } firefox)
            yield return ("firefox", firefox);
    }

    public static string? Resolve(string browser)
    {
        return browser.ToLowerInvariant() switch
        {
            "auto" => ResolveAuto().Select(b => b.Path).FirstOrDefault(),
            "edge" => FindEdge(),
            "chrome" => FindChrome(),
            "firefox" => FindFirefox(),
            _ => null,
        };
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);

    private static string? Which(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var line = p?.StandardOutput.ReadLine()?.Trim();
            p?.WaitForExit(5000);
            return !string.IsNullOrEmpty(line) && File.Exists(line) ? line : null;
        }
        catch
        {
            return null;
        }
    }
}
