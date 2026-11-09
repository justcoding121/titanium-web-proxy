using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: dual-wildcard Hostnames intersect, compound Duration parse,
///     and Conditions generation fields on conflict status.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave21BehaviorTests
{
    [TestMethod]
    public void Hostnames_DualWildcardIntersect_ReturnsMoreSpecific()
    {
        Assert.IsTrue(Hostnames.TryIntersect("*.api.example", "*.example", out var result));
        Assert.AreEqual("*.api.example", result);
        Assert.IsTrue(Hostnames.TryIntersect("*.example", "*.api.example", out var reverse));
        Assert.AreEqual("*.api.example", reverse);
        Assert.IsFalse(Hostnames.TryIntersect("*.foo.example", "*.bar.example", out _));
    }

    [TestMethod]
    public void DurationParser_HoursAndMilliseconds()
    {
        Assert.IsTrue(DurationParser.TryParse("1h500ms", out var span));
        Assert.AreEqual(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(500), span);
        Assert.IsFalse(DurationParser.TryParse("not-a-duration", out _));
    }

    [TestMethod]
    public void Conditions_Make_RecordsObservedGeneration()
    {
        var conflict = Conditions.Make(Conditions.Conflicted, false, "Overlap", "route", 42);
        Assert.AreEqual(Conditions.Conflicted, conflict.Type);
        Assert.AreEqual("False", conflict.Status);
        Assert.AreEqual(42, conflict.ObservedGeneration);
    }

    [TestMethod]
    public void BackendTls_EmptyWellKnownCa_SkipsHttpsClient()
    {
        var b = Basic();
        var policy = BackendTls("empty-ca", "default", "web", "web.svc", wellKnownCa: "");
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }
}
