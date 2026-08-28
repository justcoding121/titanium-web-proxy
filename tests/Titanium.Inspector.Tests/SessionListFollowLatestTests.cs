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
    public void ShouldScrollToLatest_OnlyWhenFollowingUnsortedAndHasItems()
    {
        Assert.IsTrue(SessionListFollowLatest.ShouldScrollToLatest(true, true, true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: false, unsorted: true, hasItems: true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: true, unsorted: false, hasItems: true));
        Assert.IsFalse(SessionListFollowLatest.ShouldScrollToLatest(followLatest: true, unsorted: true, hasItems: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_Programmatic_DoesNotChangeState()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: true, userMovedOffset: true, isNearBottom: false, allContentVisible: false));
        Assert.IsFalse(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: true, userMovedOffset: true, isNearBottom: true, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_ExtentOnly_DoesNotUnpin()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: false, userMovedOffset: false, isNearBottom: false, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_UserLeavesBottom_Pauses()
    {
        Assert.IsFalse(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: true, programmatic: false, userMovedOffset: true, isNearBottom: false, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_UserReturnsToBottom_Resumes()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: false, userMovedOffset: true, isNearBottom: true, allContentVisible: false));
    }

    [TestMethod]
    public void UpdateFollowAfterScroll_EmptyOrFullyVisible_Resumes()
    {
        Assert.IsTrue(SessionListFollowLatest.UpdateFollowAfterScroll(
            currentlyFollowing: false, programmatic: false, userMovedOffset: false, isNearBottom: true, allContentVisible: true));
    }

    [TestMethod]
    public void ShouldResumeFollowAfterReset_OnlyWhenEmpty()
    {
        Assert.IsTrue(SessionListFollowLatest.ShouldResumeFollowAfterReset(0));
        Assert.IsFalse(SessionListFollowLatest.ShouldResumeFollowAfterReset(3));
    }
}
