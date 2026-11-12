using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Pure hostname / duration / selector / ReferenceGrant helpers used by every translator pass.
///     Bug classes: wildcard intersect leaks broad names, duration parse accepts garbage, selectors ignore NotIn/Exists.
/// </summary>
[TestClass]
public class GatewayTranslatorHelpersBehaviorTests
{
    [TestMethod]
    public void Hostnames_Intersect_RanksAndNormalizes()
    {
        Assert.IsTrue(Hostnames.TryIntersect(null, "App.Example", out var anyLeft));
        Assert.AreEqual("app.example", anyLeft);
        Assert.IsTrue(Hostnames.TryIntersect("App.Example", null, out var anyRight));
        Assert.AreEqual("app.example", anyRight);
        Assert.IsTrue(Hostnames.TryIntersect("app.example", "APP.EXAMPLE", out var exact));
        Assert.AreEqual("app.example", exact);

        Assert.IsTrue(Hostnames.TryIntersect("*.example", "www.example", out var wildExact));
        Assert.AreEqual("www.example", wildExact);
        Assert.IsTrue(Hostnames.TryIntersect("api.example", "*.example", out var exactWild));
        Assert.AreEqual("api.example", exactWild);
        Assert.IsTrue(Hostnames.TryIntersect("*.svc.example", "*.example", out var nestedWild));
        Assert.AreEqual("*.svc.example", nestedWild);
        Assert.IsFalse(Hostnames.TryIntersect("a.example", "b.example", out _));
        Assert.IsFalse(Hostnames.TryIntersect("*.a.example", "*.b.example", out _));

        Assert.AreEqual(2, Hostnames.Rank(null));
        Assert.AreEqual(2, Hostnames.Rank(""));
        Assert.AreEqual(1, Hostnames.Rank("*.example"));
        Assert.AreEqual(0, Hostnames.Rank("app.example"));
        Assert.IsNull(Hostnames.Normalize("*"));
        Assert.IsNull(Hostnames.Normalize(""));
        Assert.AreEqual("host", Hostnames.Normalize("HOST"));
    }

    [TestMethod]
    public void DurationParser_AcceptsCompoundGatewayDurations()
    {
        Assert.IsTrue(DurationParser.TryParse("500ms", out var ms));
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), ms);
        Assert.IsTrue(DurationParser.TryParse("1m30s", out var compound));
        Assert.AreEqual(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30), compound);
        Assert.IsTrue(DurationParser.TryParse("1h", out var hour));
        Assert.AreEqual(TimeSpan.FromHours(1), hour);

        Assert.IsFalse(DurationParser.TryParse(null, out _));
        Assert.IsFalse(DurationParser.TryParse("", out _));
        Assert.IsFalse(DurationParser.TryParse("30", out _));
        Assert.IsFalse(DurationParser.TryParse("1d", out _));
        Assert.IsFalse(DurationParser.TryParse("999999ms", out _), "more than five digits must fail the shape regex");
    }

    [TestMethod]
    public void Selectors_MatchLabelsAndExpressions()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app"] = "web",
            ["tier"] = "front",
        };

        Assert.IsTrue(Selectors.Matches(null, labels));
        Assert.IsTrue(Selectors.Matches(new LabelSelector { MatchLabels = new() { ["app"] = "web" } }, labels));
        Assert.IsFalse(Selectors.Matches(new LabelSelector { MatchLabels = new() { ["app"] = "api" } }, labels));

        Assert.IsTrue(Selectors.Matches(new LabelSelector
        {
            MatchExpressions =
            [
                new LabelSelectorRequirement { Key = "tier", Operator = "In", Values = ["front", "edge"] },
                new LabelSelectorRequirement { Key = "missing", Operator = "DoesNotExist" },
                new LabelSelectorRequirement { Key = "app", Operator = "Exists" },
            ],
        }, labels));

        Assert.IsTrue(Selectors.Matches(new LabelSelector
        {
            MatchExpressions =
            [
                new LabelSelectorRequirement { Key = "tier", Operator = "NotIn", Values = ["back"] },
            ],
        }, labels));
        Assert.IsFalse(Selectors.Matches(new LabelSelector
        {
            MatchExpressions =
            [
                new LabelSelectorRequirement { Key = "tier", Operator = "NotIn", Values = ["front"] },
            ],
        }, labels));
        Assert.IsFalse(Selectors.Matches(new LabelSelector
        {
            MatchExpressions =
            [
                new LabelSelectorRequirement { Key = "tier", Operator = "Unknown" },
            ],
        }, labels));
    }

    [TestMethod]
    public void GrantIndex_AllowsNamedAndWildcardTargets()
    {
        var grants = new List<ReferenceGrant>
        {
            new()
            {
                Metadata = new ObjectMeta { Name = "g1", Namespace = "backend" },
                Spec = new ReferenceGrantSpec
                {
                    From =
                    [
                        new ReferenceGrantFrom
                        {
                            Group = GatewayApiNames.Group,
                            Kind = GatewayApiNames.HttpRouteKind,
                            Namespace = "edge",
                        },
                    ],
                    To =
                    [
                        new ReferenceGrantTo
                        {
                            Group = "",
                            Kind = "Service",
                            Name = "web",
                        },
                        new ReferenceGrantTo
                        {
                            Group = "",
                            Kind = "Service",
                        },
                    ],
                },
            },
        };
        var index = new GrantIndex(grants);

        Assert.IsTrue(index.Allows(
            GatewayApiNames.Group,
            GatewayApiNames.HttpRouteKind,
            "edge",
            "",
            "Service",
            "backend",
            "web"));
        Assert.IsTrue(index.Allows(
            GatewayApiNames.Group,
            GatewayApiNames.HttpRouteKind,
            "edge",
            "",
            "Service",
            "backend",
            "any"));
        Assert.IsFalse(index.Allows(
            GatewayApiNames.Group,
            GatewayApiNames.HttpRouteKind,
            "other",
            "",
            "Service",
            "backend",
            "web"));
        Assert.IsFalse(index.Allows(
            GatewayApiNames.Group,
            GatewayApiNames.HttpRouteKind,
            "edge",
            "",
            "Service",
            "other",
            "web"));
    }
}
