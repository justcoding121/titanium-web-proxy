using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
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

    [TestMethod]
    public async Task EligibilityMatrix_UnsetKnobsStayLite_ConfiguredKnobsLeaveIt()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
        };
        var request = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        request.RequestUriString8 = "/".GetByteString();
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
        };
        await manager.ApplyAsync([cluster]);
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = [route],
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
        };

        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, request, false, false, false, UpstreamHttpProtocol.Http11));

        foreach (var upstream in new[] { UpstreamHttpProtocol.Http2, UpstreamHttpProtocol.Http3 })
        {
            Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false, upstream), upstream.ToString());
        }

        proxy.ReverseProxy = Options(manager, new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Mirror = new MirrorConfig { ClusterId = "m" },
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false, null));

        proxy.ReverseProxy = Options(manager, new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Retry = new RetryConfig { Attempts = 1 },
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false, null));

        var keyed = new ClusterConfig
        {
            Id = "c",
            HttpClient = new ClusterHttpClientConfig { MaxConnectionsPerServer = 4 },
            Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
        };
        await manager.ApplyAsync([keyed]);
        proxy.ReverseProxy = Options(manager, new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false, null));

        await manager.ApplyAsync([cluster]);
        var rated = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Policies = ["rate"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = [rated],
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            Policies = [new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1000, WindowSeconds = 1 }],
        };
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, request, false, false, false, null));

        rated = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Policies = ["slots"],
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = [rated],
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            Policies = [new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 8 }],
        };
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, request, false, false, false, null));
    }

    private static ReverseProxyOptions Options(ClusterManager manager, RouteConfig route) => new()
    {
        Routes = [route],
        ClusterManager = manager,
        LoadBalancer = new LoadBalancer(),
    };
}
