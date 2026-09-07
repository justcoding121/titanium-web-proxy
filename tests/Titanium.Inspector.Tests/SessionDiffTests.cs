using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionDiffTests
{
    [TestMethod]
    public void Compare_Identical_ReportsNoDiff()
    {
        var a = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            Url = "https://a/x",
            StatusCode = 200,
            RequestHeadersText = "Accept: */*\r\nHost: a\r\n",
            ResponseBodyText = "ok",
        };
        var b = new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            Url = "https://a/x",
            StatusCode = 200,
            RequestHeadersText = "Host: a\r\nAccept: */*\r\n",
            ResponseBodyText = "ok",
        };
        var result = SessionDiff.Compare(a, b);
        Assert.IsFalse(result.HasDifferences);
        StringAssert.Contains(result.Text, "= (identical)");
    }

    [TestMethod]
    public void Compare_BodyChange_ReportsDiff()
    {
        var left = new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/", StatusCode = 200, ResponseBodyText = "v1" };
        var right = new SessionSnapshot { Id = 2, Method = "GET", Url = "https://a/", StatusCode = 200, ResponseBodyText = "v2" };
        var result = SessionDiff.Compare(left, right);
        Assert.IsTrue(result.HasDifferences);
        StringAssert.Contains(result.Text, "- v1");
        StringAssert.Contains(result.Text, "+ v2");
    }

    [TestMethod]
    public void Compare_HeaderAndStatus_ReportsDiff()
    {
        var left = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            Url = "https://a/",
            StatusCode = 200,
            RequestHeadersText = "X-A: 1\r\nAccept: */*\r\n",
        };
        var right = new SessionSnapshot
        {
            Id = 2,
            Method = "POST",
            Url = "https://a/",
            StatusCode = 404,
            RequestHeadersText = "Accept: */*\r\nX-A: 2\r\nX-B: new\r\n",
        };
        var result = SessionDiff.Compare(left, right);
        Assert.IsTrue(result.HasDifferences);
        StringAssert.Contains(result.Text, "Method: GET → POST");
        StringAssert.Contains(result.Text, "Status: 200 → 404");
        StringAssert.Contains(result.Text, "- X-A: 1");
        StringAssert.Contains(result.Text, "+ X-A: 2");
        StringAssert.Contains(result.Text, "+ X-B: new");
    }
}
