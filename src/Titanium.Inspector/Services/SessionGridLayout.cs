using System.ComponentModel;

namespace Titanium.Inspector.Services;

/// <summary>Persisted width / order for one sessions-grid column.</summary>
public sealed class SessionGridColumnStateDto
{
    public string Key { get; set; } = "";
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}

/// <summary>Persisted sessions-grid column layout and active sort.</summary>
public sealed class SessionGridLayoutDto
{
    public List<SessionGridColumnStateDto> Columns { get; set; } = new();
    public string? SortColumnKey { get; set; }
    public ListSortDirection? SortDirection { get; set; }
}

/// <summary>
/// Pure helpers for sessions-grid layout persistence and factory-default sort (Id ascending).
/// </summary>
public static class SessionGridLayout
{
    public const string DefaultSortColumnKey = "Id";

    public static string? GetColumnKey(object? header) => header as string;

    /// <summary>
    /// Factory default is Id ascending. Persisted sort wins when both key and direction are set.
    /// </summary>
    public static void ResolveSort(
        SessionGridLayoutDto? layout,
        out string columnKey,
        out ListSortDirection direction)
    {
        if (layout?.SortColumnKey is { Length: > 0 } key
            && layout.SortDirection is { } dir)
        {
            columnKey = key;
            direction = dir;
            return;
        }

        columnKey = DefaultSortColumnKey;
        direction = ListSortDirection.Ascending;
    }

    public static Dictionary<string, SessionGridColumnStateDto> IndexByKey(
        IEnumerable<SessionGridColumnStateDto>? columns)
    {
        var map = new Dictionary<string, SessionGridColumnStateDto>(StringComparer.Ordinal);
        if (columns is null)
        {
            return map;
        }

        foreach (var column in columns)
        {
            if (string.IsNullOrEmpty(column.Key))
            {
                continue;
            }

            map[column.Key] = column;
        }

        return map;
    }

    /// <summary>
    /// Prefer measured ActualWidth; fall back to absolute Width.Value.
    /// </summary>
    public static double ResolvePersistableWidth(double actualWidth, bool widthIsAbsolute, double absoluteWidth)
    {
        if (actualWidth > 0)
        {
            return actualWidth;
        }

        if (widthIsAbsolute && absoluteWidth > 0)
        {
            return absoluteWidth;
        }

        return 0;
    }
}
