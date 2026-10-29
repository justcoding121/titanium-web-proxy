using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Generated clusters, including broken ones: the rendered config must always pass the data plane's validator,
///     and whatever the translator cannot honor must show up in a status condition instead of a config error.
/// </summary>
[TestClass]
public class GatewayTranslatorPropertyTests
{
    private static readonly string[] Hosts = ["a.example.com", "b.example.com", "*.example.com", "other.org", "*.org"];
    private static readonly string[] PathValues = ["/", "/a", "/a/", "/a/b", "/v1", "/c", "/d/e", "/[0-9]+", "x", "/("];
    private static readonly string[] PathTypes = ["Exact", "PathPrefix", "PathPrefix", "PathPrefix", "RegularExpression", "Bogus"];
    private static readonly string[] FilterTypes =
        ["RequestHeaderModifier", "ResponseHeaderModifier", "URLRewrite", "RequestRedirect", "RequestMirror", "ExtensionRef", "Odd"];

    [TestMethod]
    public void RandomClusters_AlwaysRenderAValidConfig()
    {
        var routed = 0;
        var answered500 = 0;
        var listeners = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var state = Generate(random);
            var result = new GatewayTranslator(Options).Render(state);
            var errors = TwpConfigValidator.Validate(result.Config);
            Assert.AreEqual(0, errors.Count, $"seed {seed}: {string.Join("; ", errors)}");

            // Round trip through the loader the data plane uses.
            var loaded = TwpConfigLoader.LoadYaml(TwpConfigLoader.ToJson(result.Config));
            Assert.AreEqual(0, TwpConfigValidator.Validate(loaded).Count, $"seed {seed} after reload");

            // Same input, same output.
            Assert.AreEqual(
                TwpConfigLoader.ToJson(result.Config),
                TwpConfigLoader.ToJson(new GatewayTranslator(Options).Render(state).Config),
                $"seed {seed} is not deterministic");

            AssertEveryRouteHasAStatus(state, result);
            AssertRoutesReferenceDefinedClusters(result);
            routed += result.Config.UserRoutes().Count(r => r.DirectResponse is null);
            answered500 += result.Config.UserRoutes().Count(r => r.DirectResponse is not null);
            listeners += result.Config.Listeners.Count;
        }

        // The generator must reach both the happy path and the rejected one, or the property proves nothing.
        Assert.IsTrue(routed > 200, $"only {routed} routed routes");
        Assert.IsTrue(answered500 > 200, $"only {answered500} 500 routes");
        Assert.IsTrue(listeners > 200, $"only {listeners} listeners");
    }

    private static void AssertEveryRouteHasAStatus(ClusterState state, RenderResult result)
    {
        var patched = result.StatusPatches.Where(p => p.Kind == StatusKind.HttpRoute).Select(p => p.Name).ToHashSet();
        foreach (var route in state.HttpRoutes.Where(r => r.Spec.ParentRefs is { Count: > 0 }))
        {
            Assert.IsTrue(patched.Contains(route.Metadata.Name), $"route {route.Metadata.Name} has no status");
        }
    }

    private static void AssertRoutesReferenceDefinedClusters(RenderResult result)
    {
        var ids = result.Config.Clusters.Select(c => c.Id).ToHashSet();
        foreach (var route in result.Config.UserRoutes().Where(r => r.Redirect is null && r.DirectResponse is null))
        {
            Assert.IsTrue(ids.Contains(route.ClusterId), $"route {route.Id} points at {route.ClusterId}");
        }
    }

    private static ClusterState Generate(Random random)
    {
        var b = Basic();
        b.Namespaces.Add(new NamespaceInfo("apps", Labels("apps")));
        b.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        foreach (var ns in new[] { "default", "apps" })
        {
            for (var i = 0; i < 4; i++)
            {
                var count = random.Next(4) == 0 ? 0 : random.Next(1, 4);
                b.AddService(ns, "svc" + i, 8080, Enumerable.Range(0, count).Select(n => $"10.{i}.0.{n + 1}").ToArray());
            }
        }

        b.Gateways[0].Spec.Listeners[0].AllowedRoutes = new AllowedRoutes { Namespaces = new RouteNamespaces { From = random.Next(4) == 0 ? "Same" : "All" } };
        b.Gateways[0].Spec.Listeners =
        [
            random.Next(5) == 0 ? Http("h", 80, Pick(random, Hosts, true)) : Http("h", 80),
            random.Next(2) == 0 ? Https("s", 443, Pick(random, Hosts, true), random.Next(2) == 0 ? "cert" : "none") : Http("o", 9999),
        ];

        for (var i = 0; i < random.Next(0, 5); i++)
        {
            var ns = random.Next(3) == 0 ? "apps" : "default";
            var route = Route("route" + i, ns, random.Next(2) == 0 ? null : [Pick(random, Hosts, false)!], Enumerable.Range(0, random.Next(1, 4)).Select(_ => RandomRule(random)).ToArray());
            b.Routes.Add(route);
        }

        return b.Build();
    }

    private static HttpRouteRule RandomRule(Random random)
    {
        var rule = new HttpRouteRule
        {
            Matches = Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomMatch(random)).ToList(),
            BackendRefs = Enumerable.Range(0, random.Next(8) == 0 ? 0 : random.Next(1, 3)).Select(_ => new HttpBackendRef
            {
                Name = "svc" + random.Next(4),
                Port = random.Next(10) == 0 ? 1 : 8080,
                Namespace = random.Next(4) == 0 ? "apps" : null,
                Weight = random.Next(4) == 0 ? 0 : random.Next(1, 10),
            }).ToList(),
            Filters = Enumerable.Range(0, random.Next(3) == 0 ? random.Next(1, 3) : 0).Select(_ => RandomFilter(random)).ToList(),
        };
        if (random.Next(4) == 0)
        {
            rule.Timeouts = new HttpRouteTimeouts
            {
                Request = Pick(random, ["1s", "1s", "0s", "bad", "1500ms"], false),
                BackendRequest = Pick(random, ["2s", "bad", null], true),
            };
        }

        return rule;
    }

    private static HttpRouteMatch RandomMatch(Random random) => new()
    {
        Path = random.Next(3) == 0 ? null : new HttpPathMatch { Type = Pick(random, PathTypes, false), Value = Pick(random, PathValues, false) },
        Method = random.Next(3) == 0 ? "GET" : null,
        Headers = random.Next(3) == 0 ? [new HttpHeaderMatch { Name = "X-H", Value = "v", Type = random.Next(6) == 0 ? "RegularExpression" : null }] : null,
        QueryParams = random.Next(3) == 0
            ? [new HttpQueryParamMatch { Name = "q", Value = Pick(random, ["1", "[0-9]+", "(?=x)", "("], false), Type = random.Next(2) == 0 ? "RegularExpression" : null }]
            : null,
    };

    private static HttpRouteFilter RandomFilter(Random random)
    {
        var type = Pick(random, FilterTypes, false)!;
        return new HttpRouteFilter
        {
            Type = type,
            RequestHeaderModifier = new HttpHeaderFilter { Set = [new HttpHeader { Name = "X", Value = "1" }] },
            ResponseHeaderModifier = new HttpHeaderFilter { Remove = ["Server"] },
            UrlRewrite = new HttpUrlRewrite
            {
                Hostname = random.Next(2) == 0 ? "rewrite.test" : null,
                Path = random.Next(2) == 0
                    ? null
                    : new HttpPathModifier
                    {
                        Type = Pick(random, ["ReplaceFullPath", "ReplacePrefixMatch", "Weird"], false)!,
                        ReplaceFullPath = random.Next(2) == 0 ? "/full" : null,
                        ReplacePrefixMatch = random.Next(2) == 0 ? "/pre" : null,
                    },
            },
            RequestRedirect = new HttpRequestRedirect
            {
                Scheme = Pick(random, ["http", "https", "ftp", null], true),
                Hostname = random.Next(2) == 0 ? "redirect.test" : null,
                StatusCode = Pick(random, [301, 302, 307], false),
                Port = random.Next(2) == 0 ? 8443 : null,
                Path = random.Next(2) == 0 ? null : new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = "/r" },
            },
            RequestMirror = new HttpRequestMirror
            {
                BackendRef = new BackendObjectReference { Name = "svc" + random.Next(4), Port = 8080 },
                Percent = random.Next(2) == 0 ? random.Next(0, 101) : null,
            },
        };
    }

    private static T Pick<T>(Random random, T[] values, bool allowNull) =>
        allowNull && random.Next(3) == 0 ? default! : values[random.Next(values.Length)];
}
