using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
/// A misspelled or half-written policy is skipped at run time, which turns a protection into a no-op.
/// Each of these cases has to fail when the configuration loads.
/// </summary>
[TestClass]
public class PolicyAndRouteValidationTests
{
    private static ClusterConfig Cluster(string id) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = 9 }],
    };

    private static RouteConfig Route(string id, string cluster = "c", params string[] policies) => new()
    {
        Id = id,
        ClusterId = cluster,
        Policies = policies.Length == 0 ? null : policies,
        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
    };

    private static string Errors(TwpConfig config) => string.Join("\n", TwpConfigValidator.Validate(config));

    [TestMethod]
    public void Route_NamingAnUndefinedPolicy_IsRejected()
    {
        var config = new TwpConfig { Clusters = [Cluster("c")], Routes = [Route("r", "c", "ghost")] };
        StringAssert.Contains(Errors(config), "unknown policy 'ghost'");
    }

    [TestMethod]
    public void Policies_WithUnknownKindOrMissingParameters_AreRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "a", Kind = "rateLimt" },
                new RoutePolicyConfig { Name = "b", Kind = "apiKey", HeaderName = "X-Key" },
                new RoutePolicyConfig { Name = "c1", Kind = "maxRequestBodySize" },
                new RoutePolicyConfig { Name = "d", Kind = "requireHeader" },
                new RoutePolicyConfig { Name = "e", Kind = "timeout" },
                new RoutePolicyConfig { Name = "f", Kind = "rateLimit", Limit = 0, WindowSeconds = 0 },
                new RoutePolicyConfig { Name = "g", Kind = "ipAllowDeny" },
                new RoutePolicyConfig { Name = "h", Kind = "ipAllowDeny", Deny = "10.0.0.0/8, bogus" },
                new RoutePolicyConfig { Name = "a", Kind = "cors" },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "unknown kind 'rateLimt'");
        StringAssert.Contains(errors, "'b' (apiKey) needs headerName and headerValue");
        StringAssert.Contains(errors, "'c1' (maxRequestBodySize)");
        StringAssert.Contains(errors, "'d' (requireHeader) needs headerName");
        StringAssert.Contains(errors, "'e' (timeout)");
        StringAssert.Contains(errors, "limit must be >= 1");
        StringAssert.Contains(errors, "windowSeconds must be >= 1");
        StringAssert.Contains(errors, "'g' (ipAllowDeny) needs allow or deny");
        StringAssert.Contains(errors, "'bogus' is not an address or CIDR prefix");
        StringAssert.Contains(errors, "Duplicate policy name 'a'");
    }

    [TestMethod]
    public void WellFormedPolicies_AreAccepted()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Key", HeaderValue = "v" },
                new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 5, WindowSeconds = 10 },
                new RoutePolicyConfig { Name = "ip", Kind = "ipAllowDeny", Allow = "10.0.0.0/8,2001:db8::/32" },
                new RoutePolicyConfig { Name = "cap", Kind = "maxRequestBodySize", MaxRequestBodyBytes = 10 },
                new RoutePolicyConfig { Name = "t", Kind = "timeout", TimeoutSeconds = 5 },
                new RoutePolicyConfig { Name = "cors", Kind = "cors" },
                new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 3 },
                new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant" },
            ],
            Routes = [Route("r", "c", "key", "rate", "ip", "cap", "t", "cors", "slots", "need")],
        };

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }

    [TestMethod]
    public void Regexes_ThatDoNotCompile_AreRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                new RouteConfig
                {
                    Id = "path",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "([", PathKind = PathMatchKind.Regex },
                },
                new RouteConfig
                {
                    Id = "tpl",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/u/{id:regex((?=a)a)}", PathKind = PathMatchKind.Template },
                },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "Route 'path' has a path regex that does not compile");
        StringAssert.Contains(errors, "Route 'tpl' template constraint");
    }

    [TestMethod]
    public void Mirror_AndRedirect_AreChecked()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                new RouteConfig
                {
                    Id = "m",
                    ClusterId = "c",
                    Mirror = new MirrorConfig { ClusterId = "nope", SamplePercent = 150 },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
                new RouteConfig
                {
                    Id = "rd",
                    ClusterId = "c",
                    Redirect = new RedirectConfig { Status = 200, Host = "x.test" },
                    Match = new RouteMatch { Path = "/r", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "mirrors to unknown cluster 'nope'");
        StringAssert.Contains(errors, "samplePercent must be 0-100");
        StringAssert.Contains(errors, "redirect status 200 is not a 3xx code");
    }
}
