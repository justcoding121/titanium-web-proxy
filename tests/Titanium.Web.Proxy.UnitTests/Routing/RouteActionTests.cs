using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class RouteActionTests
{
    [TestMethod]
    public void Redirect_BuildsAnAbsoluteLocationAndSkipsTheCluster()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "",
                    Redirect = new RedirectConfig { Status = 301, Host = "example.test", Path = "/new", PreserveQuery = true },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual(301, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("http://example.test/new?x=1", session.HttpClient.Response.Headers.GetHeaders("Location")![0].Value);
    }

    [TestMethod]
    public void DirectResponse_SetsStatusAndBody()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "",
                    DirectResponse = new DirectResponseConfig { Status = 204, Body = "" },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual(204, session.HttpClient.Response.StatusCode);
    }

    [TestMethod]
    public void RequireHeader_DeniesBeforeAClusterIsNeeded()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies =
            [
                new RoutePolicyConfig { Name = "key", Kind = "requireHeader", HeaderName = "X-Api-Key", HeaderValue = "secret" },
            ],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "missing",
                    Policies = ["key"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy);
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual(401, session.HttpClient.Response.StatusCode);
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://old.test/old?x=1";
        return session;
    }
}
