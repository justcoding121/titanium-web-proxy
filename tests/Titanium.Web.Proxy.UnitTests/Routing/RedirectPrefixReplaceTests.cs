using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

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

    /// <summary>The same rows as the Gateway URLRewrite spec table, so a redirect and a rewrite cannot drift.</summary>
    [TestMethod]
    [DataRow("/abc", "/abc/def?x=1", "/xyz/def?x=1")]
    [DataRow("/abc", "/abc", "/xyz")]
    [DataRow("/abc", "/abc?x=1", "/xyz?x=1")]
    [DataRow("/abc", "/abc/", "/xyz/")]
    public void FollowsTheSamePrefixTableAsUrlRewrite(string prefix, string input, string expected)
    {
        var from = prefix.TrimEnd('/');
        Assert.AreEqual("https://dest.test" + expected, Location(input, from, "/xyz"));
    }

    [TestMethod]
    public void EdgeCases_RootPrefix_EmptyReplacement_Case_AndEncoding()
    {
        Assert.AreEqual("https://dest.test/new/x", Location("/x", string.Empty, "/new"));
        Assert.AreEqual("https://dest.test/x", Location("/old/x", "/old", string.Empty));
        Assert.AreEqual("https://dest.test/", Location("/old", "/old", string.Empty));
        Assert.AreEqual("https://dest.test/new/", Location("/old/", "/old", "/new"));
        // Origin-form normalization collapses a leading "//" before the prefix check, so this is "/old/x".
        Assert.AreEqual("https://dest.test/new/x", Location("//old/x", "/old", "/new"));
        Assert.AreEqual("https://dest.test/OLD/x", Location("/OLD/x", "/old", "/new"));
        Assert.AreEqual("https://dest.test/new/%2e%2e", Location("/old/%2e%2e", "/old", "/new"));
    }

    [TestMethod]
    public void UnsetPrefixFields_KeepThePreviousRedirect()
    {
        var request = new Request { Method = "GET", Host = "app.test" };
        request.RequestUriString8 = (ByteString)"/old/a?q=1";
        var full = ReverseProxySessionDispatch.BuildRedirect(request, new RedirectConfig
        {
            Host = "dest.test",
            Scheme = "https",
            Path = "/fixed",
        });
        var same = ReverseProxySessionDispatch.BuildRedirect(request, new RedirectConfig
        {
            Host = "dest.test",
            Scheme = "https",
        });
        Assert.AreEqual("https://dest.test/fixed?q=1", full);
        Assert.AreEqual("https://dest.test/old/a?q=1", same);
    }

    [TestMethod]
    [DataRow(301)]
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(307)]
    [DataRow(308)]
    public void SessionPath_WritesTheConfiguredStatusAndLocation(int status)
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString8 = (ByteString)"/old/a?q=1";
        ReverseProxySessionDispatch.PrepareTerminalResponse(session, new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Redirect = new RedirectConfig
            {
                Status = status,
                Host = "dest.test",
                Scheme = "https",
                ReplacePrefixFrom = "/old",
                ReplacePrefixTo = "/new",
            },
        });

        Assert.AreEqual(status, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("https://dest.test/new/a?q=1", session.HttpClient.Response.Headers.GetHeaders("Location")![0].Value);
    }
}
