using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class HttpInterceptionGateTests
{
    private static HttpInterceptionContext CreateContext(ProxyEndPoint endPoint) =>
        new()
        {
            Hostname = "example.com",
            Port = 443,
            IsHttps = true,
            Method = "GET",
            PathAndQuery = "/",
            HttpVersion = HttpVersion.Version11,
            ProxyEndPoint = endPoint
        };

    [TestMethod]
    public void NeedsHttpInterception_False_WhenNoHandlersAndFlagOff()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.NeedsHttpInterception());
        Assert.IsFalse(proxy.NeedsHttpInterception(null));
    }

    [TestMethod]
    public void NeedsHttpInterception_True_WhenBeforeRequestSubscribed()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.BeforeRequest += NoOpSessionHandler;
        Assert.IsTrue(proxy.NeedsHttpInterception());
        proxy.BeforeRequest -= NoOpSessionHandler;
    }

    [TestMethod]
    public void NeedsHttpInterception_True_WhenEnableHttpInterceptionSet()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttpInterception = true
        };
        Assert.IsTrue(proxy.NeedsHttpInterception());
    }

    [TestMethod]
    public void NeedsHttpInterception_True_WhenEndpointOverrideSet()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            EnableHttpInterception = true
        };
        Assert.IsTrue(proxy.NeedsHttpInterception(endPoint));
        Assert.IsFalse(proxy.NeedsHttpInterception());
    }

    [TestMethod]
    public void ShouldIntercept_NullPredicate_InterceptsAllWhenGateOn()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttpInterception = true
        };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        Assert.IsTrue(proxy.ShouldIntercept(CreateContext(endPoint), endPoint));
    }

    [TestMethod]
    public void ShouldIntercept_PredicateFalse_ReturnsFalseWhenGateOn()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttpInterception = true,
            ShouldInterceptHttp = _ => false
        };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        Assert.IsFalse(proxy.ShouldIntercept(CreateContext(endPoint), endPoint));
    }

    [TestMethod]
    public void ShouldIntercept_GateOff_ReturnsFalseRegardlessOfPredicate()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            ShouldInterceptHttp = _ => true
        };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        Assert.IsFalse(proxy.ShouldIntercept(CreateContext(endPoint), endPoint));
    }

    private static Task NoOpSessionHandler(object sender, SessionEventArgs e) => Task.CompletedTask;
}
