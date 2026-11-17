using System.Diagnostics;

namespace Titanium.Inspector.InstallSmoke;

/// <summary>
/// Linux uses xdotool under Xvfb. macOS uses System Events.
/// Three attempts, then the caller may seed settings (Linux and macOS only).
/// </summary>
internal static class UiClicks
{
    public static async Task<ClickResult> ClickDecryptHttpsAsync(int pid, TimeSpan timeout)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var clicked = OperatingSystem.IsMacOS()
                ? Run("osascript", """-e "tell application \"System Events\" to tell process \"TitaniumInspector\" to click checkbox \"Decrypt HTTPS\" of toolbar 1 of window 1" """)
                : ClickLinuxToolbar();
            if (clicked)
                return new ClickResult(true, OperatingSystem.IsMacOS() ? "osascript" : "xdotool", null);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return new ClickResult(false, "click", "Decrypt HTTPS click failed after 3 attempts.");
    }

    public static async Task<ClickResult> ClickMoveToApplicationsAsync(int pid, TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOS())
            return new ClickResult(false, "n/a", "Move to Applications is macOS-only.");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (Run("osascript", """-e "tell application \"System Events\" to tell process \"TitaniumInspector\" to click button \"Move to Applications\" of window 1" """))
                return new ClickResult(true, "osascript", null);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return new ClickResult(false, "click", "Move to Applications click failed after 3 attempts.");
    }

    private static bool ClickLinuxToolbar()
    {
        // Fixed geometry, then a click in the toolbar band. The caller polls settings.json.
        var id = Capture("xdotool", "search --name \"Titanium Inspector\"");
        if (string.IsNullOrWhiteSpace(id))
            id = Capture("xdotool", "search --class TitaniumInspector");
        var window = id.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(window))
            return false;
        Run("xdotool", $"windowsize {window} 1280 800");
        Run("xdotool", $"windowmove {window} 40 40");
        return Run("xdotool", $"mousemove --window {window} 140 70 click 1");
    }

    private static bool Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (p is null || !p.WaitForExit(8000))
                return false;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Capture(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null || !p.WaitForExit(8000))
                return "";
            return p.StandardOutput.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }
}

internal readonly record struct ClickResult(bool Ok, string How, string? Error);
