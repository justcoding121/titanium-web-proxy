using System.Diagnostics;
using Avalonia.Styling;

namespace Titanium.Inspector.Services;

/// <summary>
/// Automatic theme. Windows and macOS keep <see cref="ThemeVariant.Default"/> so Avalonia
/// follows UISettings and NSAppearance, including later changes.
/// Linux does the same when the desktop portal reports light or dark. When the portal is
/// missing or has no preference, GTK and KDE settings decide.
/// </summary>
internal static class OsThemePreference
{
    public static ThemeVariant AutomaticVariant()
    {
        if (!OperatingSystem.IsLinux())
        {
            return ThemeVariant.Default;
        }

        var scheme = TryReadPortalColorScheme();
        if (scheme is 1 or 2)
        {
            return ThemeVariant.Default;
        }

        return FilePrefersDark() ? ThemeVariant.Dark : ThemeVariant.Default;
    }

    internal static bool FilePrefersDark()
    {
        var config = ConfigDirectory();
        return PrefersDark(
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("GTK_THEME"),
            ReadIfExists(Path.Combine(config, "gtk-4.0", "settings.ini")),
            ReadIfExists(Path.Combine(config, "gtk-3.0", "settings.ini")),
            ReadIfExists(Path.Combine(config, "kdeglobals")));
    }

    internal static bool PrefersDark(string? desktop, string? gtkThemeEnv, string? gtk4Ini, string? gtk3Ini, string? kdeGlobals)
    {
        var fromEnv = GtkThemeEnvPrefersDark(gtkThemeEnv);
        if (fromEnv is not null)
        {
            return fromEnv.Value;
        }

        var gtk = GtkSettingsPrefersDark(gtk4Ini) ?? GtkSettingsPrefersDark(gtk3Ini);
        var kde = KdePrefersDark(kdeGlobals);
        var isKde = desktop is not null && desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);
        if (isKde)
        {
            return kde ?? gtk ?? false;
        }

        return gtk ?? kde ?? false;
    }

    internal static bool? GtkThemeEnvPrefersDark(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Contains(":dark", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Contains(":light", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains("dark", StringComparison.OrdinalIgnoreCase) ? true : null;
    }

    internal static bool? GtkSettingsPrefersDark(string? iniText)
    {
        if (string.IsNullOrEmpty(iniText))
        {
            return null;
        }

        bool? prefer = null;
        string? theme = null;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '[')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"');
            if (key.Equals("gtk-application-prefer-dark-theme", StringComparison.OrdinalIgnoreCase))
            {
                prefer = val is "1" or "true" or "yes";
            }
            else if (key.Equals("gtk-theme-name", StringComparison.OrdinalIgnoreCase))
            {
                theme = val;
            }
        }

        if (prefer is not null)
        {
            return prefer;
        }

        if (theme is null)
        {
            return null;
        }

        return theme.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool? KdePrefersDark(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string? scheme = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            const string key = "ColorScheme=";
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                scheme = line[key.Length..].Trim();
            }
        }

        return scheme is null ? null : scheme.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Portal <c>color-scheme</c>: 0 no preference, 1 dark, 2 light. Null when the portal cannot be read.</summary>
    internal static int? ParseColorScheme(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var marker = "uint32";
        var index = output.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = output[(index + marker.Length)..].TrimStart();
            var length = 0;
            while (length < rest.Length && char.IsDigit(rest[length]))
            {
                length++;
            }

            if (length > 0 && int.TryParse(rest[..length], out var scheme) && scheme is >= 0 and <= 2)
            {
                return scheme;
            }
        }

        return null;
    }

    private static int? TryReadPortalColorScheme()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/gdbus",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("call");
            process.StartInfo.ArgumentList.Add("--session");
            process.StartInfo.ArgumentList.Add("--dest");
            process.StartInfo.ArgumentList.Add("org.freedesktop.portal.Desktop");
            process.StartInfo.ArgumentList.Add("--object-path");
            process.StartInfo.ArgumentList.Add("/org/freedesktop/portal/desktop");
            process.StartInfo.ArgumentList.Add("--method");
            process.StartInfo.ArgumentList.Add("org.freedesktop.portal.Settings.Read");
            process.StartInfo.ArgumentList.Add("org.freedesktop.appearance");
            process.StartInfo.ArgumentList.Add("color-scheme");
            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }

                return null;
            }

            return ParseColorScheme(process.StandardOutput.ReadToEnd());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static string ConfigDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
    }

    private static string? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
