using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Remaining SessionSearch leftovers toward 85%: hide:static, status ranges with pending,
///     BuildSessionCountText retention hint, and ContainsToken / RemoveToken bare-word edges.
/// </summary>
[TestClass]
public class SessionSearchCountAndHideCoverageBehaviorTests
{
    [TestMethod]
    public void Filter_HideStatic_AndStatusRange_CoverPendingAndBareExclusion()
    {
        var sessions = new List<SessionSnapshot>
        {
            new() { Id = 1, Url = "https://a.example/app.js", StatusCode = 200, Method = "GET" },
            new() { Id = 2, Url = "https://a.example/api", StatusCode = 200, Method = "GET" },
            new() { Id = 3, Url = "https://a.example/x", StatusCode = null, Method = "GET" },
            new() { Id = 4, Url = "https://noise.example/", StatusCode = 500, Method = "POST" },
        };

        var noStatic = SessionSearch.Filter(sessions, "hide:static").Select(s => s.Id).ToList();
        CollectionAssert.DoesNotContain(noStatic, 1L);
        CollectionAssert.Contains(noStatic, 2L);

        Assert.AreEqual(2, SessionSearch.Filter(sessions, "status:200-299").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "status:5xx").Count());
        Assert.AreEqual(0, SessionSearch.Filter(sessions, "status:2xx").Count(s => s.StatusCode is null));

        Assert.AreEqual(3, SessionSearch.Filter(sessions, "-noise").Count());
        Assert.IsTrue(SessionSearch.Matches(sessions[1], "api"));
        Assert.IsFalse(SessionSearch.Matches(sessions[1], "-api"));
    }

    [TestMethod]
    public void BuildSessionCountText_RetentionHint_AndFormatSinceOtherDay()
    {
        var oldest = new DateTimeOffset(new DateTime(2026, 3, 9, 10, 15, 0, DateTimeKind.Local));
        var now = new DateTimeOffset(new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Local));
        var sinceOther = SessionSearch.FormatSince(oldest, now);
        StringAssert.Contains(sinceOther, "Mar");

        var text = SessionSearch.BuildSessionCountText(
            visibleCount: 0,
            totalCount: 8,
            searchQuery: "host:missing",
            retentionEvictedTotal: 3,
            oldestStartedUtc: oldest,
            nowUtc: now);
        StringAssert.Contains(text, "0 of 8");
        StringAssert.Contains(text, "most recent");
        StringAssert.Contains(text, "match filter");
        StringAssert.Contains(text, "3 older removed by retention");
        StringAssert.Contains(text, "since ");
    }

    [TestMethod]
    public void ContainsToken_AndRemoveToken_BareWordsStayBare()
    {
        Assert.IsFalse(SessionSearch.ContainsToken(null, "host", "a"));
        Assert.IsTrue(SessionSearch.ContainsToken("host:a method:GET", "HOST", "a"));
        Assert.AreEqual("method:GET", SessionSearch.RemoveToken("host:a method:GET", "host", "a"));
        Assert.AreEqual("login method:GET", SessionSearch.RemoveToken("login method:GET", "host", "missing"));
        Assert.AreEqual("", SessionSearch.RemoveToken("   ", "host", "a"));
    }
}
