using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Fat leftovers in TranslationPass.Status / Matches: pending Programmed and bad header regex.
/// </summary>
[TestClass]
public class GatewayTranslatorStatusAndMatchesBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    [TestMethod]
    public void StatusPatches_StayPendingUntilDataPlaneConfirmsHash()
    {
        var result = Basic().Render();
        var pending = Cond(result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Conditions, "Programmed");
        Assert.AreEqual("False", pending.Status);
        Assert.AreEqual("Pending", pending.Reason);
        StringAssert.Contains(pending.Message, "data plane");

        const string hash = "cfg-hash";
        var unconfirmed = result.Finalize(new DataPlaneObservation([null, hash]), hash);
        Assert.AreEqual("Pending", Cond(unconfirmed.Single(p => p.Kind == StatusKind.Gateway).Conditions, "Programmed").Reason);

        var confirmed = result.Finalize(new DataPlaneObservation([hash, hash]), hash);
        Assert.AreEqual("True", Cond(confirmed.Single(p => p.Kind == StatusKind.Gateway).Conditions, "Programmed").Status);
    }

    [TestMethod]
    public void HeaderRegex_ThatDoesNotCompile_Answers500()
    {
        var b = Basic();
        b.Routes.Add(Route(
            "r",
            "default",
            null,
            new HttpRouteRule
            {
                Matches =
                [
                    new HttpRouteMatch
                    {
                        Headers = [new HttpHeaderMatch { Name = "x", Value = "(?=x)", Type = "RegularExpression" }],
                    },
                ],
                BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
            }));

        var result = b.Render();
        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
        Assert.AreEqual("True", Cond(result.StatusPatches.Single(p => p.Kind == StatusKind.HttpRoute).Parents!.Single().Conditions, "PartiallyInvalid").Status);
    }

    [TestMethod]
    public void QueryRegex_ThatDoesNotCompile_Answers500()
    {
        var b = Basic();
        b.Routes.Add(Route(
            "r",
            "default",
            null,
            new HttpRouteRule
            {
                Matches =
                [
                    new HttpRouteMatch
                    {
                        QueryParams = [new HttpQueryParamMatch { Name = "q", Value = "(", Type = "RegularExpression" }],
                    },
                ],
                BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
            }));

        Assert.AreEqual(500, b.Render().Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void Ingress_StatusPatch_CarriesDataPlaneAddress_AndProgramsRoute()
    {
        var b = IngressOnly();
        b.Ingresses.Add(Ingress("shop", "titanium", [IRule("shop.example", IPath("/", "Prefix"))]));

        var result = b.Render();
        var ingress = result.StatusPatches.Single(p => p.Kind == StatusKind.Ingress);
        Assert.AreEqual("shop", ingress.Name);
        Assert.AreEqual("203.0.113.10", ingress.Addresses!.Single().Value);
        Assert.IsTrue(result.Config.UserRoutes().Any(r =>
            r.Match?.Host == "shop.example" ||
            (r.Match?.Hosts?.Contains("shop.example") ?? false)));
    }
}
