using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class H1TerminateLiteEligibilityTests
{
    [TestMethod]
    public void CanUseH1TerminateLite_False_WhenUpstreamIsHttp2OrHttp3()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "origin.local",
            ForwardPort = 80
        };
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11
        };
        request.RequestUriString8 = "/".GetByteString();

        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false,
            UpstreamHttpProtocol.Http2));
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false,
            UpstreamHttpProtocol.Http3));
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, request, false, false, false,
            UpstreamHttpProtocol.Http11));
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, request, false, false, false,
            UpstreamHttpProtocol.Auto));
    }

    [TestMethod]
    public void CanUseH1TerminateLite_False_ForPostOrMissingForwardHost()
    {
        using var proxy = new ProxyServer(false, false, false);
        var noForward = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);
        var withForward = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "origin.local",
            ForwardPort = 80
        };
        var get = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        get.RequestUriString8 = "/".GetByteString();
        var post = new Request { Method = "POST", HttpVersion = HttpHeader.Version11 };
        post.RequestUriString8 = "/".GetByteString();
        post.ContentLength = 4;

        Assert.IsFalse(proxy.CanUseH1TerminateLite(noForward, get, false, false, false, null));
        Assert.IsFalse(proxy.CanUseH1TerminateLite(withForward, post, false, false, false, null));
    }
}
