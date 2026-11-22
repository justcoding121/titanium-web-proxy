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

        // Install / Replace / OpenExisting use different accept labels.
        var buttons = new[] { "Move to Applications", "Update Applications", "Open Applications copy" };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            foreach (var label in buttons)
            {
                if (Run("osascript",
                        $"""-e "tell application \"System Events\" to tell process \"TitaniumInspector\" to click button \"{label}\" of window 1" """))
                    return new ClickResult(true, "osascript:" + label, null);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return new ClickResult(false, "click", "DMG relocate click failed after 3 attempts.");
    }

    /// <summary>
    /// The AppImage install dialog is centered on the main window. "Not Now" is the
    /// right-hand button. A deb install has no dialog; the click lands on the empty grid.
    /// </summary>
    public static Task DismissAppImageInstallAsync()
    {
        if (OperatingSystem.IsLinux())
            ClickAppImageNotNow();
        return Task.CompletedTask;
    }

    public static void ClickAppImageNotNow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var id = Capture("xdotool", "search --name \"Titanium Inspector\"");
        var window = id.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(window))
            return;
        Run("xdotool", $"windowactivate {window}");
        var geom = Capture("xdotool", $"getwindowgeometry --shell {window}");
        var x = 0;
        var y = 0;
        var w = 0;
        var h = 0;
        foreach (var line in geom.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var n))
                continue;
            switch (parts[0])
            {
                case "X": x = n; break;
                case "Y": y = n; break;
                case "WIDTH": w = n; break;
                case "HEIGHT": h = n; break;
            }
        }

        if (w > 0 && h > 0)
            Run("xdotool", $"mousemove {x + w / 2 + 120} {y + h / 2 + 70} click 1");
    }

    public static bool ClickLinuxPoint(int x, int y)
    {
        var id = Capture("xdotool", "search --name \"Titanium Inspector\"");
        if (string.IsNullOrWhiteSpace(id))
            id = Capture("xdotool", "search --class TitaniumInspector");
        var window = id.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(window))
            return false;
        Run("xdotool", $"windowactivate {window}");
        return Run("xdotool", $"mousemove --window {window} {x} {y} click 1");
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
        // Checkbox row sits under the search field. 140,70 lands in the search box
        // on the AppImage window and never toggles Decrypt HTTPS.
        return Run("xdotool", $"mousemove --window {window} 190 115 click 1");
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
