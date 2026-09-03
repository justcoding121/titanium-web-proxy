namespace Titanium.Inspector.Services;

/// <summary>Newline-separated host pattern helpers for settings UI.</summary>
public static class HostListFormat
{
    public static string Join(IEnumerable<string>? hosts) =>
        hosts is null ? "" : string.Join(Environment.NewLine, hosts.Where(h => !string.IsNullOrWhiteSpace(h)));

    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h.Length > 0 && !h.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
