using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Redirect <c>ReplacePrefixFrom</c>/<c>ReplacePrefixTo</c>: Gateway API <c>ReplacePrefixMatch</c>.</summary>
[TestClass]
public class RedirectPrefixReplaceTests
{
    private static string Location(string target, string from, string to, bool preserveQuery = true)
    {
        var request = new Request { Method = "GET", Host = "app.test" };
        request.RequestUriString8 = (ByteString)target;
        return ReverseProxySessionDispatch.BuildRedirect(request, new RedirectConfig
        {
            Host = "dest.test",
            Scheme = "https",
            ReplacePrefixFrom = from,
            ReplacePrefixTo = to,
            PreserveQuery = preserveQuery,
        });
    }

    [TestMethod]
    public void ReplacesThePrefixAndKeepsTheRest()
    {
        Assert.AreEqual("https://dest.test/new/a/b", Location("/old/a/b", "/old", "/new"));
    }

    [TestMethod]
    public void TheBarePrefixMapsToTheReplacement()
    {
        Assert.AreEqual("https://dest.test/new", Location("/old", "/old", "/new"));
    }

    [TestMethod]
    public void AnEmptyReplacementDropsThePrefix_AndNeverLeavesAnEmptyPath()
    {
        Assert.AreEqual("https://dest.test/a", Location("/old/a", "/old", string.Empty));
        Assert.AreEqual("https://dest.test/", Location("/old", "/old", string.Empty));
    }

    [TestMethod]
    public void KeepsTheQueryString()
    {
        Assert.AreEqual("https://dest.test/new/x?a=1&b=2", Location("/old/x?a=1&b=2", "/old", "/new"));
        Assert.AreEqual("https://dest.test/new?a=1", Location("/old?a=1", "/old", "/new"));
    }

    [TestMethod]
    public void MatchesOnASegmentBoundaryOnly()
    {
        Assert.AreEqual("https://dest.test/oldish/x", Location("/oldish/x", "/old", "/new"));
    }

    [TestMethod]
    public void TheRootPrefixPrependsTheReplacement()
    {
        Assert.AreEqual("https://dest.test/new/a", Location("/a", string.Empty, "/new"));
    }

    [TestMethod]
    public void ReplaceFullPathWinsOverAPrefixReplacement()
    {
        var request = new Request { Method = "GET", Host = "app.test" };
        request.RequestUriString8 = (ByteString)"/old/a";
        var location = ReverseProxySessionDispatch.BuildRedirect(request, new RedirectConfig
        {
            Host = "dest.test",
            Scheme = "https",
            Path = "/fixed",
            ReplacePrefixFrom = "/old",
            ReplacePrefixTo = "/new",
        });

        Assert.AreEqual("https://dest.test/fixed", location);
    }
}
