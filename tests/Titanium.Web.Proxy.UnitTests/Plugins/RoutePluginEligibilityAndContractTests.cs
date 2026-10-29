using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Plugins;
using static Titanium.Web.Proxy.UnitTests.Plugins.PluginTestKit;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary>A route that names a plugin leaves terminate-lite; a route that does not keeps it. Plus the plugin contract's compatibility pins.</summary>
[TestClass]
public class RoutePluginEligibilityAndContractTests
{
    [TestMethod]
    public async Task OnlyARouteThatNamesAPlugin_LeavesTerminateLite()
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
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
            },
        ]);

        using var host = PluginHost.Load([Plugin("rec", "RecordingPlugin")]);

        ReverseProxyOptions Options(string[]? middleware) => new()
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Middleware = middleware,
                },
            ],
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
            RoutePlugins = host.Plugins,
        };

        proxy.ReverseProxy = Options(null);
        Assert.IsTrue(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null),
            "a loaded plugin that no route names costs the lite path nothing");

        proxy.ReverseProxy = Options(["rec"]);
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null),
            "a route that names a plugin needs the session");

        proxy.ReverseProxy = Options([]);
        Assert.IsTrue(proxy.CanUseH1TerminateLite(endPoint, request, false, false, false, null),
            "an empty list names nothing");
    }

    [TestMethod]
    public void TheContractVersionAndTheSamplePluginAgree()
    {
        Assert.AreEqual(1, ProxyPluginContract.CurrentVersion, "raising the version is a deliberate, documented break");
        Assert.IsTrue(ProxyPluginContract.MinimumSupportedVersion <= ProxyPluginContract.CurrentVersion);

        using var host = PluginHost.Load(
        [
            new PluginConfig { Name = "sample", Assembly = SamplePlugin },
        ]);
        Assert.AreEqual(1, host.Plugins.Count, "the sample plugin loads at the current contract version");

        var plugin = host.Plugins["sample"];
        Assert.IsNotNull(plugin);
        Assert.IsNull(PluginTypeResolver.Probe(SamplePlugin, null));
        Assert.IsNull(PluginTypeResolver.Probe(TestPlugins, "Titanium.Web.Proxy.TestPlugins.RecordingPlugin"));
        StringAssert.Contains(
            PluginTypeResolver.Probe(TestPlugins, "Titanium.Web.Proxy.TestPlugins.FutureContractPlugin")!,
            "contract version 99");
    }

    /// <summary>
    /// The surface a plugin compiles against. A change here breaks plugins built for the current contract, so it must come
    /// with a contract version bump and a note in the plugin guide.
    /// </summary>
    [TestMethod]
    public void TheMiddlewareSurfaceAPluginCompilesAgainstIsPinned()
    {
        var middleware = typeof(IProxyMiddleware);
        Assert.IsTrue(middleware.IsInterface && middleware.IsPublic);
        var invoke = middleware.GetMethods().Single();
        Assert.AreEqual("InvokeAsync", invoke.Name);
        Assert.AreEqual(typeof(ValueTask), invoke.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(ProxyMiddlewareContext), typeof(ProxyMiddlewareDelegate), typeof(CancellationToken) },
            invoke.GetParameters().Select(p => p.ParameterType).ToArray());

        var next = typeof(ProxyMiddlewareDelegate).GetMethod("Invoke")!;
        Assert.AreEqual(typeof(ValueTask), next.ReturnType);
        CollectionAssert.AreEqual(
            new[] { typeof(ProxyMiddlewareContext), typeof(CancellationToken) },
            next.GetParameters().Select(p => p.ParameterType).ToArray());

        var members = typeof(ProxyMiddlewareContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        foreach (var required in new[]
                 {
                     "Session", "Items", "IsHandled", "HandledStatusCode", "HandledBody", "HandledBodyBytes", "HandledHeaders",
                 })
        {
            Assert.IsTrue(members.Contains(required), $"ProxyMiddlewareContext.{required} is part of the plugin contract");
        }

        var config = typeof(PluginConfig).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "Assembly", "BodyMode", "CalloutHosts", "FailMode", "File", "Kind", "Limits", "Name", "Settings", "TimeoutMs",
                "TypeName", "VmId",
            },
            config);

        var attribute = typeof(ProxyPluginAttribute);
        Assert.IsNotNull(attribute.GetConstructor([typeof(int)]));
        Assert.IsTrue(attribute.GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn.HasFlag(AttributeTargets.Class));
    }
}
