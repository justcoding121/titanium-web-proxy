namespace Titanium.Inspector.Services;

/// <summary>
/// Viewport rules for the session grid sticky-bottom (follow latest) behavior.
/// Follow newest rows while the user is at the bottom; pause when they scroll away;
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

    public static bool ShouldScrollToLatest(bool followLatest, bool unsorted, bool hasItems) =>
        followLatest && unsorted && hasItems;

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
        bool isNearBottom,
        bool allContentVisible)
    {
        if (programmatic)
        {
            return currentlyFollowing;
        }

        if (userMovedOffset)
        {
            return isNearBottom;
        }

        return allContentVisible || currentlyFollowing;
    }
}
