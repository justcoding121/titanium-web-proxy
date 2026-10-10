using System.Globalization;

namespace Titanium.Inspector.Localization;

/// <summary>
/// Operating-system UI language for Automatic mode. Does not change the thread culture.
/// Windows uses <see cref="CultureInfo.CurrentUICulture"/> (the user display language).
/// macOS uses the preferred-language list, which a terminal <c>LANG</c> would otherwise hide.
/// Linux uses gettext's <c>LANGUAGE</c> first, then the process locale, then <c>locale.conf</c>.
/// </summary>
internal static class OsUiLanguage
{
    internal readonly record struct Probe(
        bool IsWindows,
        bool IsMac,
        bool IsLinux,
        string? CurrentUiCultureName,
        string? LanguageVariable,
        string? MacPreferred,
        string? LocaleConfLang);

    public static CultureInfo Detect()
    {
        foreach (var tag in CandidateTags(Capture()))
        {
            if (TryGetCulture(tag, out var culture))
            {
                return culture;
            }
        }

        return CultureInfo.CurrentUICulture;
    }

    internal static Probe Capture()
    {
        var current = CultureInfo.CurrentUICulture.Name;
        string? mac = null;
        string? localeConf = null;
        if (OperatingSystem.IsMacOS())
        {
            mac = MacPreferredLanguage.TryGetFirst();
        }
        else if (OperatingSystem.IsLinux() && NormalizeTag(current) is null)
        {
            localeConf = ReadLocaleConfLang();
        }

        return new Probe(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsLinux(),
            current,
            Environment.GetEnvironmentVariable("LANGUAGE"),
            mac,
            localeConf);
    }

    internal static IEnumerable<string> CandidateTags(Probe probe)
    {
        if (probe.IsMac)
        {
            var mac = NormalizeTag(probe.MacPreferred);
            if (mac is not null)
            {
                yield return mac;
            }
        }
        else if (probe.IsLinux)
        {
            var language = NormalizeTag(FirstListEntry(probe.LanguageVariable));
            if (language is not null)
            {
                yield return language;
            }
        }

        var current = NormalizeTag(probe.CurrentUiCultureName);
        if (current is not null)
        {
            yield return current;
        }

        if (probe.IsLinux)
        {
            var file = NormalizeTag(probe.LocaleConfLang);
            if (file is not null)
            {
                yield return file;
            }
        }
    }

    /// <summary>First gettext list entry, without encoding or modifier (<c>pt_BR.UTF-8@euro</c> → <c>pt_BR</c>).</summary>
    internal static string? FirstListEntry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var entry = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entry.Length == 0 ? null : entry[0];
    }

    /// <summary><c>de_DE.UTF-8</c> → <c>de-DE</c>. <c>C</c> and <c>POSIX</c> are not languages.</summary>
    internal static string? NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var token = raw.Trim().Trim('"');
        var at = token.IndexOf('@');
        if (at >= 0)
        {
            token = token[..at];
        }

        var dot = token.IndexOf('.');
        if (dot >= 0)
        {
            token = token[..dot];
        }

        token = token.Replace('_', '-').Trim();
        if (token.Length == 0
            || token.Equals("C", StringComparison.OrdinalIgnoreCase)
            || token.Equals("POSIX", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return token;
    }

    internal static string? ReadLangAssignment(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string? lang = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var value = Assignment(line, "LANG")
                ?? Assignment(line, "LC_ALL")
                ?? Assignment(line, "LC_MESSAGES");
            if (value is not null)
            {
                lang = value;
                if (line.StartsWith("LANG=", StringComparison.Ordinal))
                {
                    return lang;
                }
            }
        }

        return lang;
    }

    private static string? ReadLocaleConfLang()
    {
        try
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var dir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            var path = Path.Combine(dir, "locale.conf");
            return File.Exists(path) ? ReadLangAssignment(File.ReadAllText(path)) : null;
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

    private static string? Assignment(string line, string key)
    {
        var prefix = key + "=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return line[prefix.Length..].Trim().Trim('"');
    }

    private static bool TryGetCulture(string tag, out CultureInfo culture)
    {
        try
        {
            culture = CultureInfo.GetCultureInfo(tag);
            return true;
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
            return false;
        }
    }
}
