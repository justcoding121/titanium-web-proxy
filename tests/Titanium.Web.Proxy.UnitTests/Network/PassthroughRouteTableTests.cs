using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class PassthroughRouteTableTests
{
    private static PassthroughRouteTable Table(params (string Name, string Cluster)[] routes) =>
        new(routes.Select(r => new PassthroughSniRoute(r.Name, r.Cluster)).ToArray());

    [TestMethod]
    public void ExactName_MatchesCaseInsensitively()
    {
        var table = Table(("api.example.com", "api"));

        Assert.AreEqual("api", table.Match("api.example.com")?.ClusterId);
        Assert.AreEqual("api", table.Match("API.Example.COM")?.ClusterId);
        Assert.IsNull(table.Match("www.example.com"));
    }

    [TestMethod]
    public void Wildcard_MatchesAnyDepthBelowTheSuffix_ButNotTheApex()
    {
        var table = Table(("*.example.com", "web"));

        Assert.AreEqual("web", table.Match("a.example.com")?.ClusterId);
        Assert.AreEqual("web", table.Match("a.b.example.com")?.ClusterId);
        Assert.IsNull(table.Match("example.com"));
        Assert.IsNull(table.Match("notexample.com"));
        Assert.IsNull(table.Match("a.example.org"));
    }

    [TestMethod]
    public void ExactBeatsWildcard_AndLongestWildcardBeatsShorter_RegardlessOfOrder()
    {
        var table = Table(
            ("*.example.com", "short"),
            ("*.api.example.com", "long"),
            ("pinned.api.example.com", "exact"));

        Assert.AreEqual("exact", table.Match("pinned.api.example.com")?.ClusterId);
        Assert.AreEqual("long", table.Match("v1.api.example.com")?.ClusterId);
        Assert.AreEqual("long", table.Match("a.b.api.example.com")?.ClusterId);
        Assert.AreEqual("short", table.Match("www.example.com")?.ClusterId);
    }

    [TestMethod]
    public void WhenAnExactNameIsListedTwice_TheFirstWins()
    {
        var table = Table(("a.example.com", "first"), ("A.example.com", "second"));

        Assert.AreEqual("first", table.Match("a.example.com")?.ClusterId);
    }

    [TestMethod]
    public void EndPoint_AdmitsPassthroughWithRoutesOrDefaultClusterEvenWithoutForwardHost()
    {
        var plain = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { Passthrough = true };
        Assert.IsFalse(plain.HasPassthroughTarget);

        var withRoutes = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            Passthrough = true,
            PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "c")],
        };
        Assert.IsTrue(withRoutes.HasPassthroughTarget);

        var withDefault = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            Passthrough = true,
            PassthroughDefaultClusterId = "c",
        };
        Assert.IsTrue(withDefault.HasPassthroughTarget);

        var withForward = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            Passthrough = true,
            ForwardHost = "127.0.0.1",
        };
        Assert.IsTrue(withForward.HasPassthroughTarget);

        var notPassthrough = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1" };
        Assert.IsFalse(notPassthrough.HasPassthroughTarget);
    }
}
