using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: Hostnames case fold on intersect, BackendTls SubjectAltNames skip,
///     and null TargetRefs leaving HTTPS unset.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave20BehaviorTests
{
    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void Hostnames_ExactMatch_FoldsCaseOnIntersect()
    {
        Assert.IsTrue(Hostnames.TryIntersect("App.Example", "app.example", out var same));
        Assert.AreEqual("app.example", same);
        Assert.IsTrue(Hostnames.TryIntersect("*.API.Example", "*.api.example", out var wild));
        Assert.AreEqual("*.api.example", wild);
    }

    [TestMethod]
    public void BackendTls_SubjectAltNames_SkipsHttpsOnCluster()
    {
        var b = Basic();
        var policy = BackendTls("san", "default", "web", "web.svc", wellKnownCa: "System");
        policy.Spec.Validation!.SubjectAltNames = ["dns:web.svc.cluster.local"];
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void BackendTls_NullTargetRefs_DoesNotApplyHttps()
    {
        var b = Basic();
        var policy = BackendTls("wide", "default", "web", "web.svc", wellKnownCa: "System");
        policy.Spec.TargetRefs = null;
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void DurationParser_CompoundMinutesAndSeconds()
    {
        Assert.IsTrue(DurationParser.TryParse("2m30s", out var span));
        Assert.AreEqual(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30), span);
    }
}
