namespace Titanium.Inspector.Services;

/// <summary>One sessions-grid column the user can show or hide.</summary>
/// <param name="Key">Stable layout key (matches <see cref="SessionGridLayout.GetColumnKey"/> for the XAML header).</param>
/// <param name="MenuLabel">Text in the Columns menus.</param>
/// <param name="DefaultVisible">Visible with no saved override (today's default grid).</param>
/// <param name="CanHide">
/// False for the request identity pair (Host and URL). URL shows only the path (empty for CONNECT tunnels) and
/// Host shows the host, so hiding either would leave rows without a clear target.
/// </param>
/// <param name="PlatformGated">Shown only where the OS can supply the data (Process).</param>
/// <param name="MenuAutomationId">AutomationId of the Options &gt; Columns menu item.</param>
public sealed record SessionGridColumnInfo(
    string Key,
    string MenuLabel,
    bool DefaultVisible,
    bool CanHide,
    bool PlatformGated,
    string MenuAutomationId);

/// <summary>
/// Pure catalog of sessions-grid columns and their default visibility. New optional columns are
/// appended after the original ten so saved <c>DisplayIndex</c> values keep restoring unchanged.
/// </summary>
public static class SessionGridColumnCatalog
{
    private static readonly SessionGridColumnInfo[] Columns =
    [
        new("Id", "Id", true, true, false, "MenuColumn_Id"),
        new("Method", "Method", true, true, false, "MenuColumn_Method"),
        new("Status", "Status", true, true, false, "MenuColumn_Status"),
        new("Host", "Host", true, false, false, "MenuColumn_Host"),
        new("URL", "URL", true, false, false, "MenuColumn_URL"),
        new("Protocol", "Protocol", true, true, false, "MenuColumn_Protocol"),
        new("Duration", "Duration", true, true, false, "MenuColumn_Duration"),
        new("TTFB", "Wait (TTFB)", true, true, false, "MenuColumn_TTFB"),
        new("Size", "Size", true, true, false, "MenuColumn_Size"),
        new("Process", "Process", true, true, true, "MenuColumn_Process"),
        new("Started", "Started", false, true, false, "MenuColumn_Started"),
        new("Scheme", "Scheme", false, true, false, "MenuColumn_Scheme"),
        new("Content-Type", "Content-Type", false, true, false, "MenuColumn_ContentType"),
    ];

    public static IReadOnlyList<SessionGridColumnInfo> All => Columns;

    public static SessionGridColumnInfo? Find(string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : Columns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Effective visibility: a saved override wins over the default, a column that cannot be hidden is always
    /// visible, and a platform-gated column is hidden where <paramref name="platformAvailable"/> is false.
    /// Unknown keys are not visible (nothing to show).
    /// </summary>
    public static bool IsVisible(
        string? key,
        IReadOnlyDictionary<string, bool>? overrides,
        bool platformAvailable)
    {
        var info = Find(key);
        if (info is null)
        {
            return false;
        }

        if (info.PlatformGated && !platformAvailable)
        {
            return false;
        }

        if (!info.CanHide)
        {
            return true;
        }

        return overrides is not null && overrides.TryGetValue(info.Key, out var chosen)
            ? chosen
            : info.DefaultVisible;
    }

    /// <summary>
    /// Returns a copy of <paramref name="overrides"/> with <paramref name="key"/> set. Only values that differ
    /// from the default are kept, so a column added later inherits its own default. Unknown keys and columns
    /// that cannot be hidden leave the overrides unchanged.
    /// </summary>
    public static Dictionary<string, bool> WithVisibility(
        IReadOnlyDictionary<string, bool>? overrides,
        string? key,
        bool visible)
    {
        var next = overrides is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(overrides, StringComparer.Ordinal);

        var info = Find(key);
        if (info is null || !info.CanHide)
        {
            return next;
        }

        if (visible == info.DefaultVisible)
        {
            next.Remove(info.Key);
        }
        else
        {
            next[info.Key] = visible;
        }

        return next;
    }
}
