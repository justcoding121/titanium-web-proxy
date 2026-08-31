using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class RequestOriginHostPortTests
{
    [TestMethod]
    public void PathOnly_NoHostNoAuthority_ReturnsEmptyHost()
    {
        var request = new Request { Method = "GET", RequestUriString = "/" };
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual(string.Empty, host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void PathOnly_WithHostHeader_ParsesHost()
    {
        var request = new Request { Method = "GET", RequestUriString = "/x" };
        request.Headers.AddHeader("Host", "example.com");
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual("example.com", host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void PathOnly_WithHostAndPort_ParsesBoth()
    {
        var request = new Request { Method = "GET", RequestUriString = "/x" };
        request.Headers.AddHeader("Host", "example.com:8443");
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual("example.com", host);
        Assert.AreEqual(8443, port);
    }

    [TestMethod]
    public void AuthorityWithPort_PreferredOverHost()
    {
        var request = new Request
        {
            Method = "GET",
            RequestUriString = "/",
            Authority = "origin.example:9443".GetByteString()
        };
        request.Headers.AddHeader("Host", "ignored.example");
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual("origin.example", host);
        Assert.AreEqual(9443, port);
    }

    [TestMethod]
    public void AuthorityBare_UsesDefaultPort()
    {
        var request = new Request
        {
            Method = "GET",
            RequestUriString = "/",
            Authority = "origin.example".GetByteString()
        };
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual("origin.example", host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void AbsoluteUri_ParsesHostAndPort()
    {
        var request = new Request
        {
            Method = "GET",
            RequestUriString = "https://uri.example:9443/x"
        };
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual("uri.example", host);
        Assert.AreEqual(9443, port);
    }

    [TestMethod]
    public void GarbageHostFallingThrough_DoesNotThrow()
    {
        var request = new Request { Method = "GET", RequestUriString = "/" };
        // Unbracketed multi-colon host fails AuthorityParser → URI fallback on "https://" alone.
        request.Headers.AddHeader("Host", ":::bad");
        var (host, port) = request.GetOriginHostPort(443);
        Assert.AreEqual(string.Empty, host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void BracketedIpv6Host_Parses()
    {
        var request = new Request { Method = "GET", RequestUriString = "/" };
        request.Headers.AddHeader("Host", "[2001:db8::1]:443");
        var (host, port) = request.GetOriginHostPort(80);
        Assert.AreEqual("2001:db8::1", host);
        Assert.AreEqual(443, port);
    }
}
