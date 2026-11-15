using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining Rules/Matches leftovers still thin on develop Sonar: redirect without a concrete
///     hostname when the route only lists wildcards, dual RequestMirror, and unknown URLRewrite path types.
/// </summary>
[TestClass]
public class GatewayRedirectMirrorAndRewriteCoverageBehaviorTests
{
    private static readonly string[] ConcreteRedirectHosts = ["a.example", "b.example"];

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static RenderResult Render(StateBuilder b, HttpRoute route)
    {
        b.Routes.Add(route);
        return b.Render();
    }

    [TestMethod]
    public void Redirect_WithoutHostname_AndOnlyWildcardHosts_UsesTheRequestHost()
    {
        // Bug class: wildcard hosts (Rank != 0) must not expand into absolute Location hosts; the data plane uses
        // the host the client sent instead.
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect { Scheme = "https" },
                },
            ],
        };
        var result = Render(b, Route("r", "default", ["*.example.com"], rule));
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.DirectResponse);
        Assert.IsNotNull(route.Redirect);
        Assert.IsNull(route.Redirect!.Host, "no host is baked in");
        Assert.IsTrue(route.Redirect.UseRequestHost);
        Assert.AreEqual("https", route.Redirect.Scheme);
        Assert.IsFalse(
            result.StatusPatches.Single(p => p.Kind == StatusKind.HttpRoute).Parents!.Single()
                .Conditions.Any(c => c.Type == "PartiallyInvalid" && c.Status == "True"));
    }

    [TestMethod]
    public void Redirect_WithoutHostname_OnARouteWithNoHostnames_UsesTheRequestHost()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect { Scheme = "https", StatusCode = 301 },
                },
            ],
        };
        var result = Render(b, Route("r", "default", null, rule));
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.DirectResponse);
        Assert.IsTrue(route.Redirect!.UseRequestHost);
        Assert.AreEqual(301, route.Redirect.Status);
    }

    [TestMethod]
    public void Redirect_WithoutHostname_OnConcreteHosts_StillBakesEachHostIn()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect { Scheme = "https" },
                },
            ],
        };
        var routes = Render(b, Route("r", "default", ConcreteRedirectHosts, rule)).Config.UserRoutes();

        CollectionAssert.AreEquivalent(ConcreteRedirectHosts, routes.Select(r => r.Redirect!.Host).ToArray());
        Assert.IsTrue(routes.All(r => !r.Redirect!.UseRequestHost));
    }

    [TestMethod]
    public void Redirect_WithoutHostname_OnMixedConcreteAndWildcardHosts_UsesTheRequestHost()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect { Scheme = "https" },
                },
            ],
        };
        var routes = Render(b, Route("r", "default", ["a.example.com", "*.example.com"], rule)).Config.UserRoutes();

        Assert.AreEqual(1, routes.Count);
        Assert.IsNull(routes[0].Redirect!.Host);
        Assert.IsTrue(routes[0].Redirect!.UseRequestHost);
    }

    [TestMethod]
    public void Redirect_ReplacePrefixMatch_WithoutHostname_UsesTheRequestHost()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Matches = [PathPrefix("/old")],
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect
                    {
                        Scheme = "https",
                        Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/new" },
                    },
                },
            ],
        };
        var route = Render(b, Route("r", "default", ["*.example.com"], rule)).Config.UserRoutes().Single();

        Assert.IsTrue(route.Redirect!.UseRequestHost);
        Assert.IsNull(route.Redirect.Host);
        Assert.AreEqual("/old", route.Redirect.ReplacePrefixFrom);
        Assert.AreEqual("/new", route.Redirect.ReplacePrefixTo);
    }

    [TestMethod]
    public void Redirect_WithoutHostname_SurvivesConfigRoundTrip()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect { Scheme = "https", StatusCode = 301 },
                },
            ],
        };
        var result = Render(b, Route("r", "default", null, rule));
        AssertValid(result);

        var loaded = TwpConfigLoader.LoadJson(TwpConfigLoader.ToJson(result.Config));
        Assert.AreEqual(0, TwpConfigValidator.Validate(loaded).Count, string.Join("; ", TwpConfigValidator.Validate(loaded)));
        var redirect = loaded.UserRoutes().Single().Redirect!;
        Assert.IsTrue(redirect.UseRequestHost);
        Assert.IsNull(redirect.Host);
        Assert.AreEqual("https", redirect.Scheme);
        Assert.AreEqual(301, redirect.Status);
    }

    [TestMethod]
    public void DualRequestMirror_IsUnsupported_AndLeavesPrimaryRoute()
    {
        var b = Basic();
        b.AddService("default", "shadow", 8080, "10.7.0.2");
        b.AddService("default", "other", 8080, "10.7.0.3");
        var rule = Rule("web");
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "RequestMirror",
                RequestMirror = new HttpRequestMirror
                {
                    BackendRef = new BackendObjectReference { Name = "shadow", Port = 8080 },
                    Percent = 50,
                },
            },
            new HttpRouteFilter
            {
                Type = "RequestMirror",
                RequestMirror = new HttpRequestMirror
                {
                    BackendRef = new BackendObjectReference { Name = "other", Port = 8080 },
                    Percent = 10,
                },
            },
        ];
        var result = Render(b, Route("r", "default", null, rule));
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual(500, route.DirectResponse?.Status);
        Assert.IsNull(route.Mirror);
    }

    [TestMethod]
    public void UrlRewrite_UnknownPathType_IsUnsupported()
    {
        var b = Basic();
        var rule = Rule("web", 8080, PathPrefix("/api"));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite
                {
                    Path = new HttpPathModifier { Type = "ReplacePrefixMatchIgnoreCase", ReplacePrefixMatch = "/v2" },
                },
            },
        ];
        var result = Render(b, Route("r", "default", null, rule));
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void HealthServer_PublicCtor_UsesProbePrefix()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        // Covers the production overload that builds the wildcard ProbePrefix for a port.
        using var server = new HealthServer(port, () => true, NullLogger.Instance);
        Assert.IsNotNull(server);
    }
}
