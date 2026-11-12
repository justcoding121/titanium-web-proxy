using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Remaining URLRewrite leftovers still thin on develop Sonar: ReplacePrefixMatch without a
///     PathPrefix match, ReplaceFullPath success, and HTTPS Opaque Secret reject message.
/// </summary>
[TestClass]
public class GatewayUrlRewriteExactAndFullPathCoverageBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void UrlRewrite_ReplacePrefixMatch_OnExactPath_IsUnsupported()
    {
        // Bug class: ReplacePrefixMatch must require PathPrefix; Exact must not silently rewrite.
        var b = Basic();
        var rule = Rule("web", 8080, PathExact("/api"));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite
                {
                    Path = new HttpPathModifier { Type = "ReplacePrefixMatch", ReplacePrefixMatch = "/v2" },
                },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();
        AssertValid(result);

        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.IsTrue(
            result.StatusPatches.Single(p => p.Kind == StatusKind.HttpRoute).Parents!.Single()
                .Conditions.Any(c => c.Type == "PartiallyInvalid" && c.Status == "True"));
    }

    [TestMethod]
    public void UrlRewrite_ReplaceFullPath_ProgramsPathRegexReplace()
    {
        var b = Basic();
        var rule = Rule("web", 8080, PathPrefix("/old"));
        rule.Filters =
        [
            new HttpRouteFilter
            {
                Type = "URLRewrite",
                UrlRewrite = new HttpUrlRewrite
                {
                    Path = new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = "/new" },
                },
            },
        ];
        b.Routes.Add(Route("r", "default", null, rule));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNull(route.DirectResponse);
        Assert.IsTrue(route.Transforms!.Any(t =>
            string.Equals(t.Kind, "PathRegexReplace", StringComparison.OrdinalIgnoreCase) &&
            t.Parameters is not null &&
            t.Parameters.TryGetValue("replacement", out var replacement) &&
            replacement == "/new"));
    }

    [TestMethod]
    public void HttpsListener_OpaqueSecret_ReportsNotTlsSecret()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Https("https", 443, null, "opaque")];
        b.Secrets.Add(new SecretInfo("default", "opaque", "Opaque", true, true));
        var result = b.Render();
        AssertValid(result);

        var listener = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();
        Assert.AreEqual("False", Cond(listener.Conditions, "ResolvedRefs").Status);
        Assert.AreEqual("InvalidCertificateRef", Cond(listener.Conditions, "ResolvedRefs").Reason);
        StringAssert.Contains(Cond(listener.Conditions, "ResolvedRefs").Message ?? "", "kubernetes.io/tls");
        Assert.AreEqual(0, result.Config.Listeners.Count);
    }

    [TestMethod]
    public void RequestRedirect_ReplaceFullPath_WithConcreteHost_ProgramsRedirect()
    {
        var b = Basic();
        var rule = new HttpRouteRule
        {
            Matches = [PathPrefix("/")],
            Filters =
            [
                new HttpRouteFilter
                {
                    Type = "RequestRedirect",
                    RequestRedirect = new HttpRequestRedirect
                    {
                        Scheme = "https",
                        Hostname = "dest.example",
                        StatusCode = 302,
                        Path = new HttpPathModifier { Type = "ReplaceFullPath", ReplaceFullPath = "/landed" },
                    },
                },
            ],
        };
        b.Routes.Add(Route("r", "default", ["app.example"], rule));
        var result = b.Render();
        AssertValid(result);

        var route = result.Config.UserRoutes().Single();
        Assert.IsNotNull(route.Redirect);
        Assert.AreEqual(302, route.Redirect!.Status);
        Assert.AreEqual("https", route.Redirect.Scheme);
        Assert.AreEqual("dest.example", route.Redirect.Host);
        Assert.AreEqual("/landed", route.Redirect.Path);
    }
}
