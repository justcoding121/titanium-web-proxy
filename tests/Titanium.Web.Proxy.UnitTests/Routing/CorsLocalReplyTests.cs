using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class CorsLocalReplyTests
{
    private static readonly RoutePolicyConfig Cors = new()
    {
        Name = "cors",
        Kind = "cors",
        Origins = "https://app.example.com",
    };

    [TestMethod]
    public void Redirect_AndDirectResponse_EchoAListedOrigin()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = Options(new RouteConfig
        {
            Id = "r",
            ClusterId = "",
            Policies = ["cors"],
            Redirect = new RedirectConfig { Host = "example.test", Path = "/new", HttpToHttps = true },
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        var session = MakeSession(proxy, "https://app.example.com");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual("https://app.example.com", Header(session, "Access-Control-Allow-Origin"));
        Assert.AreEqual("Origin", Header(session, "Vary"));

        proxy.ReverseProxy = Options(new RouteConfig
        {
            Id = "r",
            ClusterId = "",
            Policies = ["cors"],
            DirectResponse = new DirectResponseConfig { Status = 200, Body = "hi", Headers = new System.Collections.Generic.Dictionary<string, string> { ["Vary"] = "Accept" } },
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        session = MakeSession(proxy, "https://app.example.com");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.Handled, ReverseProxySessionDispatch.Apply(proxy, session));
        Assert.AreEqual("https://app.example.com", Header(session, "Access-Control-Allow-Origin"));
        StringAssert.Contains(Header(session, "Vary"), "Accept");
        StringAssert.Contains(Header(session, "Vary"), "Origin");

        session = MakeSession(proxy, "https://evil.example");
        ReverseProxySessionDispatch.Apply(proxy, session);
        Assert.IsNull(Header(session, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task NoDestination_OriginFailure_AndMissingPlugin_CarryCors()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = Options(new RouteConfig
        {
            Id = "r",
            ClusterId = "missing",
            Policies = ["cors"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        var session = MakeSession(proxy, "https://app.example.com");
        Assert.AreEqual(ReverseProxySessionDispatch.RouteDispatchResult.NoDestination, ReverseProxySessionDispatch.Apply(proxy, session));
        ReverseProxySessionDispatch.SignalNoDestination(session);
        Assert.AreEqual(503, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("https://app.example.com", Header(session, "Access-Control-Allow-Origin"));

        var failed = MakeSession(proxy, "https://app.example.com");
        failed.MatchedRoute = proxy.ReverseProxy.Routes![0];
        OriginFailureResponses.Apply(failed, new IOException("down"));
        session = failed;
        Assert.AreEqual(502, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("https://app.example.com", Header(session, "Access-Control-Allow-Origin"));

        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Policies = ["login", "cors"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        var options = new ReverseProxyOptions
        {
            Policies =
            [
                new RoutePolicyConfig { Name = "login", Kind = "oidc" },
                Cors,
            ],
            Routes = [route],
        };
        proxy.ReverseProxy = options;
        session = MakeSession(proxy, "https://app.example.com");
        Assert.IsTrue(await PolicyHandlerDispatch.DeniedAsync(options, route, session));
        Assert.AreEqual(503, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("https://app.example.com", Header(session, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public void ARouteWithoutCors_AddsNoAllowHeader()
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
                    Redirect = new RedirectConfig { Host = "example.test", Path = "/new" },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var session = MakeSession(proxy, "https://app.example.com");
        ReverseProxySessionDispatch.Apply(proxy, session);
        Assert.IsNull(Header(session, "Access-Control-Allow-Origin"));
    }

    private static ReverseProxyOptions Options(RouteConfig route) => new()
    {
        Policies = [Cors],
        Routes = [route],
    };

    private static SessionEventArgs MakeSession(ProxyServer proxy, string origin)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://old.test/old?x=1";
        session.HttpClient.Request.Headers.AddHeader("Origin", origin);
        return session;
    }

    private static string? Header(SessionEventArgs session, string name)
    {
        var headers = session.HttpClient.Response.Headers.GetHeaders(name);
        return headers is { Count: > 0 } ? headers[0].Value : null;
    }
}
