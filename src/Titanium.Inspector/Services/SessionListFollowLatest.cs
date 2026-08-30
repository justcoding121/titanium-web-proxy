using System.ComponentModel;

namespace Titanium.Inspector.Services;

/// <summary>
/// Where new sessions appear when following the latest row.
/// Unsorted / Id ascending → bottom; Id descending → top.
/// </summary>
public enum SessionListFollowEdge
{
    None,
    Bottom,
    Top,
}

/// <summary>
/// Viewport rules for the session grid sticky follow-latest behavior.
/// Avalonia DataGrid scrolls via PART_VerticalScrollbar (not a ScrollViewer).
/// Follow newest rows while the user is at the follow edge; pause when they scroll away;
/// resume when they return. Extent growth from new rows must not unpin.
/// </summary>
public static class SessionListFollowLatest
{
    public const double DefaultThresholdPx = 32;

    public static bool IsNearBottom(
        double offset,
        double viewport,
        double extent,
        double threshold = DefaultThresholdPx)
    {
        if (extent <= viewport)
        {
            return true;
        }

        return offset + viewport >= extent - threshold;
    }

    /// <summary>
    /// Near-bottom check for DataGrid's vertical ScrollBar
    /// (<c>Maximum = extent - viewport</c>, <c>Value = offset</c>).
    /// </summary>
    public static bool IsNearBottomByScrollBar(
        double value,
        double maximum,
        double threshold = DefaultThresholdPx)
    {
        if (maximum <= 0)
        {
            return true;
        }

        return value >= maximum - threshold;
    }

    /// <summary>
    /// Near-top check for DataGrid's vertical ScrollBar (<c>Value = offset</c>).
    /// </summary>
    public static bool IsNearTopByScrollBar(
        double value,
        double maximum,
        double threshold = DefaultThresholdPx)
    {
        if (maximum <= 0)
        {
            return true;
        }

        return value <= threshold;
    }

    public static bool IsNearFollowEdgeByScrollBar(
        SessionListFollowEdge edge,
        double value,
        double maximum,
        double threshold = DefaultThresholdPx) =>
        edge switch
        {
            SessionListFollowEdge.Bottom => IsNearBottomByScrollBar(value, maximum, threshold),
            SessionListFollowEdge.Top => IsNearTopByScrollBar(value, maximum, threshold),
            _ => false,
        };

    /// <summary>
    /// Unsorted or Id ascending → stick to bottom (new rows append / sort to end).
    /// Id descending → stick to top (new high Ids appear first).
    /// Any other column sort → no auto-follow.
    /// </summary>
    public static SessionListFollowEdge ResolveFollowEdge(
        bool anyColumnSorted,
        bool idColumnIsSoleSort,
        ListSortDirection? idSortDirection)
    {
        if (!anyColumnSorted)
        {
            return SessionListFollowEdge.Bottom;
        }

        if (!idColumnIsSoleSort || idSortDirection is null)
        {
            return SessionListFollowEdge.None;
        }

        return idSortDirection == ListSortDirection.Descending
            ? SessionListFollowEdge.Top
            : SessionListFollowEdge.Bottom;
    }

    public static bool ShouldScrollToLatest(
        bool followLatest,
        SessionListFollowEdge edge,
        bool hasItems) =>
        followLatest && edge != SessionListFollowEdge.None && hasItems;

    /// <summary>
    /// Clear empties the grid and should resume follow. A filter rebuild also fires Reset
    /// then immediately re-adds; only resume when the collection is still empty afterward.
    /// </summary>
    public static bool ShouldResumeFollowAfterReset(int remainingCount) => remainingCount == 0;

    /// <summary>
    /// Updates follow state after a scroll event.
    /// Programmatic and extent-only changes (new rows) must not unpin;
    /// an empty or fully-visible list re-enables follow.
    /// </summary>
    public static bool UpdateFollowAfterScroll(
        bool currentlyFollowing,
        bool programmatic,
        bool userMovedOffset,
        bool isNearFollowEdge,
        bool allContentVisible)
    {
        if (programmatic)
        {
            return currentlyFollowing;
        }

        if (userMovedOffset)
        {
            return isNearFollowEdge;
        }

        return allContentVisible || currentlyFollowing;
    }
}
