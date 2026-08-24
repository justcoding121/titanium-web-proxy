using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class StripHopByHopConnectionTests
{
    [TestMethod]
    public void Http10_KeepAlive_IsPreserved()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version10
        };
        request.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive.String);

        request.StripHopByHopConnectionForTransparentOrigin();

        Assert.AreEqual(KnownHeaders.ConnectionKeepAlive.String,
            request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
    }

    [TestMethod]
    public void Http10_ConnectionClose_IsStripped()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version10
        };
        request.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose.String);

        request.StripHopByHopConnectionForTransparentOrigin();

        Assert.IsNull(request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
    }

    [TestMethod]
    public void Http11_ConnectionClose_IsStripped()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11
        };
        request.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose.String);

        request.StripHopByHopConnectionForTransparentOrigin();

        Assert.IsNull(request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
    }

    [TestMethod]
    public void Http11_KeepAlive_IsStripped_PersistenceIsDefault()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11
        };
        request.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive.String);

        request.StripHopByHopConnectionForTransparentOrigin();

        Assert.IsNull(request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
    }

    [TestMethod]
    public void MissingConnection_IsNoOp()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11
        };

        request.StripHopByHopConnectionForTransparentOrigin();

        Assert.IsNull(request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
    }
}
