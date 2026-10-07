using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionDisplayFormatTests
{
    [TestMethod]
    public void FormatHttpProtocol_MapsKnownVersions()
    {
        Assert.AreEqual("?", SessionDisplayFormat.FormatHttpProtocol(null));
        Assert.AreEqual("?", SessionDisplayFormat.FormatHttpProtocol(new Version(0, 0)));
        Assert.AreEqual("h1.0", SessionDisplayFormat.FormatHttpProtocol(new Version(1, 0)));
        Assert.AreEqual("h1.1", SessionDisplayFormat.FormatHttpProtocol(new Version(1, 1)));
        Assert.AreEqual("h2", SessionDisplayFormat.FormatHttpProtocol(new Version(2, 0)));
        Assert.AreEqual("h3", SessionDisplayFormat.FormatHttpProtocol(new Version(3, 0)));
    }

    [TestMethod]
    public void FormatClientServer_UsesShortNamesAndArrow()
    {
        Assert.AreEqual("h1.1", SessionDisplayFormat.FormatClientServer(new Version(1, 1), null));
        Assert.AreEqual("h1.1 → h2", SessionDisplayFormat.FormatClientServer(new Version(1, 1), new Version(2, 0)));
        Assert.AreEqual("h2 → h3", SessionDisplayFormat.FormatClientServer(new Version(2, 0), new Version(3, 0)));
        Assert.AreEqual("h1.1 → h1.1", SessionDisplayFormat.FormatClientServer(new Version(1, 1), new Version(1, 1)));
    }

    [TestMethod]
    public void FormatUrlForGrid_DropsHostThatTheHostColumnAlreadyShows()
    {
        Assert.AreEqual("/api/items?x=1", SessionDisplayFormat.FormatUrlForGrid("https://api.example.com/api/items?x=1", "api.example.com"));
        Assert.AreEqual("/api/items", SessionDisplayFormat.FormatUrlForGrid("https://API.example.com/api/items", "api.example.com"));
        Assert.AreEqual("/", SessionDisplayFormat.FormatUrlForGrid("https://example.com", "example.com"));
        Assert.AreEqual("/", SessionDisplayFormat.FormatUrlForGrid("https://example.com/", "example.com"));
        Assert.AreEqual("/?q=1", SessionDisplayFormat.FormatUrlForGrid("https://example.com?q=1", "example.com"));
        Assert.AreEqual("/ws", SessionDisplayFormat.FormatUrlForGrid("wss://ws.test/ws", "ws.test"));
        Assert.AreEqual("/a", SessionDisplayFormat.FormatUrlForGrid("http://user@h.test/a#frag", "h.test"));

        // Raw text is kept: no percent re-escaping of spaces or unicode.
        Assert.AreEqual("/a b/é?q=1 2", SessionDisplayFormat.FormatUrlForGrid("https://h.test/a b/é?q=1 2", "h.test"));
    }

    [TestMethod]
    public void FormatUrlForGrid_KeepsNonDefaultPortBecauseHostHasNone()
    {
        Assert.AreEqual(":3000/api", SessionDisplayFormat.FormatUrlForGrid("http://localhost:3000/api", "localhost"));
        Assert.AreEqual("/api", SessionDisplayFormat.FormatUrlForGrid("http://localhost:80/api", "localhost"));
        Assert.AreEqual("/api", SessionDisplayFormat.FormatUrlForGrid("https://localhost:443/api", "localhost"));
        Assert.AreEqual(":8443/", SessionDisplayFormat.FormatUrlForGrid("https://localhost:8443", "localhost"));
    }

    [TestMethod]
    public void FormatUrlForGrid_TunnelIsEmptyBecauseThereIsNoPath()
    {
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid("example.com:443", "example.com"));
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid("Example.com:8443", "example.com"));
    }

    [TestMethod]
    public void FormatUrlForGrid_FallsBackToFullUrlWhenNothingIsSafelyDropped()
    {
        Assert.AreEqual("https://other.test/x", SessionDisplayFormat.FormatUrlForGrid("https://other.test/x", "example.com"));
        Assert.AreEqual("https://example.com/x", SessionDisplayFormat.FormatUrlForGrid("https://example.com/x", null));
        Assert.AreEqual("https://example.com/x", SessionDisplayFormat.FormatUrlForGrid("https://example.com/x", ""));
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid(null, "example.com"));
        Assert.AreEqual("", SessionDisplayFormat.FormatUrlForGrid("", "example.com"));
        Assert.AreEqual("/relative/path", SessionDisplayFormat.FormatUrlForGrid("/relative/path", "example.com"));
        Assert.AreEqual("other.test:443", SessionDisplayFormat.FormatUrlForGrid("other.test:443", "example.com"));
        Assert.AreEqual("example.com:abc", SessionDisplayFormat.FormatUrlForGrid("example.com:abc", "example.com"));
        Assert.AreEqual("example.com:", SessionDisplayFormat.FormatUrlForGrid("example.com:", "example.com"));
    }

    [TestMethod]
    public void SessionSnapshot_UrlDisplayFollowsHostButUrlStaysFull()
    {
        var changed = new List<string?>();
        var s = new SessionSnapshot { Url = "https://example.com/a?b=1" };
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.AreEqual("https://example.com/a?b=1", s.UrlDisplay);

        s.Host = "example.com";
        Assert.AreEqual("/a?b=1", s.UrlDisplay);
        Assert.AreEqual("https://example.com/a?b=1", s.Url);
        CollectionAssert.Contains(changed, nameof(SessionSnapshot.UrlDisplay));
        Assert.IsTrue(SessionSearch.Matches(s, "example.com/a"), "URL search still sees the full URL");
    }

    [TestMethod]
    public void FormatByteSize_UsesBKbMb()
    {
        Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(null));
        Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(-1));
        Assert.AreEqual("0 B", SessionDisplayFormat.FormatByteSize(0));
        Assert.AreEqual("512 B", SessionDisplayFormat.FormatByteSize(512));
        Assert.AreEqual("1023 B", SessionDisplayFormat.FormatByteSize(1023));
        Assert.AreEqual("1.0 KB", SessionDisplayFormat.FormatByteSize(1024));
        Assert.AreEqual("1.5 KB", SessionDisplayFormat.FormatByteSize(1536));
        Assert.AreEqual("10 KB", SessionDisplayFormat.FormatByteSize(10 * 1024));
        Assert.AreEqual("1.0 MB", SessionDisplayFormat.FormatByteSize(1024 * 1024));
        Assert.AreEqual("2.5 MB", SessionDisplayFormat.FormatByteSize((long)(2.5 * 1024 * 1024)));
    }

    [TestMethod]
    [DataRow(null, HttpStatusClass.Pending)]
    [DataRow(100, HttpStatusClass.Informational)]
    [DataRow(199, HttpStatusClass.Informational)]
    [DataRow(200, HttpStatusClass.Success)]
    [DataRow(204, HttpStatusClass.Success)]
    [DataRow(299, HttpStatusClass.Success)]
    [DataRow(301, HttpStatusClass.Redirection)]
    [DataRow(304, HttpStatusClass.Redirection)]
    [DataRow(404, HttpStatusClass.ClientError)]
    [DataRow(418, HttpStatusClass.ClientError)]
    [DataRow(500, HttpStatusClass.ServerError)]
    [DataRow(599, HttpStatusClass.ServerError)]
    [DataRow(0, HttpStatusClass.Other)]
    [DataRow(99, HttpStatusClass.Other)]
    [DataRow(600, HttpStatusClass.Other)]
    public void GetStatusClass_MapsHttpClasses(int? statusCode, HttpStatusClass expected)
    {
        Assert.AreEqual(expected, SessionDisplayFormat.GetStatusClass(statusCode));
    }
}
