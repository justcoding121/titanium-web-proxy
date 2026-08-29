using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionListFollowLatestTests
{
    [TestMethod]
    public void IsNearBottom_WhenContentFits_IsTrue()
    {
        Assert.IsTrue(SessionListFollowLatest.IsNearBottom(0, 400, 200));
        Assert.IsTrue(SessionListFollowLatest.IsNearBottom(0, 400, 400));
    }

    [TestMethod]
    public void IsNearBottom_AtAndWithinThreshold_IsTrue()
    {
        Assert.IsTrue(SessionListFollowLatest.IsNearBottom(offset: 968, viewport: 400, extent: 1368, threshold: 32));
        Assert.IsTrue(SessionListFollowLatest.IsNearBottom(offset: 940, viewport: 400, extent: 1368, threshold: 32));
    }

    [TestMethod]
    public void IsNearBottom_WhenScrolledUp_IsFalse()
    {
        Assert.IsFalse(SessionListFollowLatest.IsNearBottom(offset: 100, viewport: 400, extent: 1368, threshold: 32));
    }

    [TestMethod]
    public void IsNearBottomByScrollBar_MatchesOffsetExtentSemantics()
    {
        Assert.IsTrue(SessionListFollowLatest.IsNearBottomByScrollBar(value: 0, maximum: 0));
        Assert.IsTrue(SessionListFollowLatest.IsNearBottomByScrollBar(value: 968, maximum: 968, threshold: 32));
        Assert.IsTrue(SessionListFollowLatest.IsNearBottomByScrollBar(value: 940, maximum: 968, threshold: 32));
        Assert.IsFalse(SessionListFollowLatest.IsNearBottomByScrollBar(value: 100, maximum: 968, threshold: 32));
    }

    [TestMethod]
    public void IsNearTopByScrollBar_MatchesOffsetSemantics()
    {
        Assert.IsTrue(SessionListFollowLatest.IsNearTopByScrollBar(value: 0, maximum: 0));
        Assert.IsTrue(SessionListFollowLatest.IsNearTopByScrollBar(value: 0, maximum: 968, threshold: 32));
        Assert.IsTrue(SessionListFollowLatest.IsNearTopByScrollBar(value: 32, maximum: 968, threshold: 32));
        Assert.IsFalse(SessionListFollowLatest.IsNearTopByScrollBar(value: 100, maximum: 968, threshold: 32));
    }

    [TestMethod]
    public void IsNearFollowEdgeByScrollBar_UsesTopOrBottom()
    {
        Assert.IsTrue(SessionListFollowLatest.IsNearFollowEdgeByScrollBar(
            SessionListFollowEdge.Top, value: 10, maximum: 968, threshold: 32));
        Assert.IsFalse(SessionListFollowLatest.IsNearFollowEdgeByScrollBar(
            SessionListFollowEdge.Top, value: 100, maximum: 968, threshold: 32));
        Assert.IsTrue(SessionListFollowLatest.IsNearFollowEdgeByScrollBar(
            SessionListFollowEdge.Bottom, value: 950, maximum: 968, threshold: 32));
        Assert.IsFalse(SessionListFollowLatest.IsNearFollowEdgeByScrollBar(
            SessionListFollowEdge.None, value: 0, maximum: 968, threshold: 32));
    }

    [TestMethod]
    public void ResolveFollowEdge_Unsorted_IsBottom()
    {
        Assert.AreEqual(
            SessionListFollowEdge.Bottom,
            SessionListFollowLatest.ResolveFollowEdge(
                anyColumnSorted: false, idColumnIsSoleSort: false, idSortDirection: null));
    }

    [TestMethod]
    public void ResolveFollowEdge_IdAscending_IsBottom()
    {
        Assert.AreEqual(
            SessionListFollowEdge.Bottom,
            SessionListFollowLatest.ResolveFollowEdge(
                anyColumnSorted: true, idColumnIsSoleSort: true, idSortDirection: ListSortDirection.Ascending));
    }

    [TestMethod]
    public void ResolveFollowEdge_IdDescending_IsTop()
    {
        Assert.AreEqual(
            SessionListFollowEdge.Top,
            SessionListFollowLatest.ResolveFollowEdge(
                anyColumnSorted: true, idColumnIsSoleSort: true, idSortDirection: ListSortDirection.Descending));
    }

    [TestMethod]
    public void ResolveFollowEdge_OtherColumn_IsNone()
    {
        Assert.AreEqual(
            SessionListFollowEdge.None,
            SessionListFollowLatest.ResolveFollowEdge(
                anyColumnSorted: true, idColumnIsSoleSort: false, idSortDirection: null));
    }

    [TestMethod]
    public void ShouldScrollToLatest_OnlyWhenFollowingHasEdgeAndItems()
    {
        Assert.IsTrue(SessionListFollowLatest.ShouldScrollToLatest(true, SessionListFollowEdge.Bottom, true));
        Assert.IsTrue(SessionListFollowLatest.ShouldScrollToLatest(true, SessionListFollowEdge.Top, true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: false, SessionListFollowEdge.Bottom, hasItems: true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: true, SessionListFollowEdge.None, hasItems: true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: true, SessionListFollowEdge.Bottom, hasItems: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_Programmatic_DoesNotChangeState()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: true, userMovedOffset: true, isNearFollowEdge: false, allContentVisible: false));
        Assert.IsFalse(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: true, userMovedOffset: true, isNearFollowEdge: true, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_ExtentOnly_DoesNotUnpin()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: false, userMovedOffset: false, isNearFollowEdge: false, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_UserLeavesEdge_Pauses()
    {
        Assert.IsFalse(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: false, userMovedOffset: true, isNearFollowEdge: false, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_UserReturnsToEdge_Resumes()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: false, userMovedOffset: true, isNearFollowEdge: true, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_EmptyOrFullyVisible_Resumes()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: false, userMovedOffset: false, isNearFollowEdge: true, allContentVisible: true));
    }

    [TestMethod]
    public void ShouldResumeFollowAfterReset_OnlyWhenEmpty()
    {
        Assert.IsTrue(SessionListFollowLatest.ShouldResumeFollowAfterReset(0));
        Assert.IsFalse(SessionListFollowLatest.ShouldResumeFollowAfterReset(3));
    }
}

[TestClass]
public class SessionIdComparerTests
{
    [TestMethod]
    public void Compare_SortsByNumericId_NotLexical()
    {
        var items = new[]
        {
            new SessionSnapshot { Id = 10 },
            new SessionSnapshot { Id = 2 },
            new SessionSnapshot { Id = 1 },
        };

        Array.Sort(items, SessionIdComparer.Instance);

        Assert.AreEqual(1, items[0].Id);
        Assert.AreEqual(2, items[1].Id);
        Assert.AreEqual(10, items[2].Id);
    }
}
