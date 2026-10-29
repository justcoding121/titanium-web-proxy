using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Scripting;

/// <summary>The default path is untouched with no script host, and any host moves the listener off terminate-lite.</summary>
[TestClass]
public class ScriptPhaseGateTests
{
    private sealed class NoopMiddleware : IProxyMiddleware
    {
        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            next(context, cancellationToken);
    }

    [TestMethod]
    public void NoHost_NothingIsGated()
    {
        var options = new ReverseProxyOptions();
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.NeedsHttpInterception());
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite());
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());

        proxy.ReverseProxy = options;
        Assert.IsFalse(proxy.NeedsHttpInterception());
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite());
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());
        Assert.AreEqual(0, proxy.LiteBlockers().Count);
    }

    [TestMethod]
    public void EmptyGlobalMiddleware_IsNotAHook()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ReverseProxy = new ReverseProxyOptions { GlobalMiddleware = [] };
        Assert.IsFalse(proxy.NeedsHttpInterception());
        Assert.AreEqual(0, proxy.LiteBlockers().Count);

        proxy.ReverseProxy = new ReverseProxyOptions { GlobalMiddleware = [new NoopMiddleware()] };
        Assert.IsTrue(proxy.NeedsHttpInterception());
        Assert.AreEqual(1, proxy.LiteBlockers().Count);
    }

    [TestMethod]
    public async Task LiteStaysEligibleWithoutAHost_AndLeavesWithOne()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
        };
        var request = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        request.RequestUriString8 = "/".GetByteString();
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig { Id = "c", Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }] },
        ]);

        ReverseProxyOptions Options(IScriptPhaseHost? host, IReadOnlyList<IProxyMiddleware>? global = null) => new()
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            ScriptPipeline = host,
            GlobalMiddleware = global,
        };

        proxy.ReverseProxy = Options(null);
        Assert.IsTrue(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null));
        Assert.AreEqual(0, proxy.LiteBlockers().Count);

        proxy.ReverseProxy = Options(new FakeScriptHost());
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null));
        Assert.IsTrue(proxy.NeedsHttpInterception());
        Assert.AreEqual(1, proxy.LiteBlockers().Count);

        proxy.ReverseProxy = Options(null, [new NoopMiddleware()]);
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null));
        Assert.IsTrue(proxy.NeedsHttpInterception());
    }

    [TestMethod]
    public void BodyWriteHookTurnsOnOnlyForStreamingHostsThatWantBodies()
    {
        using var proxy = new ProxyServer(false, false, false);

        proxy.ReverseProxy = new ReverseProxyOptions { ScriptPipeline = new FakeScriptHost() };
        Assert.IsTrue(proxy.ShouldCallBeforeRequestBodyWrite());
        Assert.IsTrue(proxy.ShouldCallBeforeResponseBodyWrite());

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ScriptPipeline = new FakeScriptHost { Phases = ScriptPhases.RequestHeaders | ScriptPhases.ResponseHeaders },
        };
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite(), "a host that does not list body phases pays nothing per chunk");
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ScriptPipeline = new FakeScriptHost { BodyMode = ScriptBodyMode.Buffered },
        };
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite(), "buffered bodies use the session buffer, not the per-chunk hook");
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ScriptPipeline = new FakeScriptHost { Phases = ScriptPhases.ResponseBody },
        };
        Assert.IsFalse(proxy.ShouldCallBeforeRequestBodyWrite());
        Assert.IsTrue(proxy.ShouldCallBeforeResponseBodyWrite());
    }

    [TestMethod]
    public void StreamFilters_NullOrEmptyKeepsThePlainRelay()
    {
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);
        Assert.IsNull(endPoint.StreamFilters);

        endPoint.StreamFilters = [];
        Assert.IsNull(endPoint.StreamFilters, "an empty list is the same as none");

        endPoint.StreamFilters = [new FakeStreamFilter()];
        Assert.AreEqual(1, endPoint.StreamFilters!.Count);
    }
}
