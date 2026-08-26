using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MitmCompressedRelayHelperTests
{
    [TestMethod]
    public void Unchanged_AllowsRelay_WithoutSnapshotDiff()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("accept", "text/html");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(headers);

        Assert.IsTrue(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, headers, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out var added));
        Assert.AreEqual(0, added.Count);
    }

    [TestMethod]
    public void AppendOneUniqueHeader_AllowsRelay()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "text/html");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "text/html");
        after.AddHeader("X-Tracking-Id", "abc");

        Assert.IsTrue(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out var added));
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual("X-Tracking-Id", added[0].Name);
        Assert.AreEqual("abc", added[0].Value);
    }

    [TestMethod]
    public void AppendTwoUniqueHeaders_AllowsRelay()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("X-A", "1");
        after.AddHeader("X-B", "2");

        Assert.IsTrue(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out var added));
        Assert.AreEqual(2, added.Count);
    }

    [TestMethod]
    public void AppendFiveUniqueHeaders_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        for (var i = 0; i < 5; i++)
            after.AddHeader($"X-H{i}", "1");

        Assert.IsFalse(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }

    [TestMethod]
    public void RemoveAndAdd_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("X-New", "1");

        Assert.IsFalse(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }

    [TestMethod]
    public void ReplaceExistingValue_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "text/plain");

        Assert.IsFalse(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }

    [TestMethod]
    public void SecondSetCookie_NonUniqueGrowth_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("cookie", "a=1");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("cookie", "a=1");
        after.AddHeader("cookie", "b=2");

        Assert.IsFalse(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baseline, after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }

    [TestMethod]
    public void MutationCountOnlyGate_Unchanged_AllowsRelay()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("accept", "*/*");
        var baselineCount = headers.MutationCount;

        Assert.IsTrue(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baselineCount, headers, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out var added));
        Assert.AreEqual(0, added.Count);
    }

    [TestMethod]
    public void MutationCountOnlyGate_Diverged_RequiresSnapshot()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("accept", "*/*");
        var baselineCount = headers.MutationCount;
        headers.AddHeader("X-Tracking-Id", "1");

        Assert.IsFalse(MitmCompressedRelayHelper.AllowsCompressedRelay(
            baselineCount, headers, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }
}
