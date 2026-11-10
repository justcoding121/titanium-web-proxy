using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: GatewayJson time formatting, ResourceParsers namespace/slice edges,
///     HTTP method/header matches, and DataPlaneObservation confirmation.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave24BehaviorTests
{
    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void GatewayJson_FormatsRfc3339AndMicroTime()
    {
        var t = new DateTimeOffset(2026, 3, 10, 12, 30, 45, TimeSpan.Zero);
        Assert.AreEqual("2026-03-10T12:30:45Z", GatewayJson.FormatTime(t));
        StringAssert.StartsWith(GatewayJson.FormatMicroTime(t), "2026-03-10T12:30:45.");
        StringAssert.EndsWith(GatewayJson.FormatMicroTime(t), "Z");
    }

    [TestMethod]
    public void ResourceParsers_Namespace_LabelsAndMissingName()
    {
        Assert.IsNull(ResourceParsers.Namespace(Parse("""{"metadata":{}}""")));
        var ns = ResourceParsers.Namespace(Parse("""
            {
              "metadata": { "name": "labeled", "labels": { "env": "prod", "tier": "data" } }
            }
            """));
        Assert.IsNotNull(ns);
        Assert.AreEqual("labeled", ns!.Name);
        Assert.AreEqual("prod", ns.Labels["env"]);
        Assert.AreEqual(2, ns.Labels.Count);
    }

    [TestMethod]
    public void ResourceParsers_EndpointSlice_ServiceLabelAndIpv6Ready()
    {
        Assert.IsNull(ResourceParsers.EndpointSlice(Parse("""
            { "metadata": { "name": "x", "namespace": "ns" }, "endpoints": [] }
            """)));

        var slice = ResourceParsers.EndpointSlice(Parse("""
            {
              "metadata": {
                "name": "web-v6",
                "namespace": "default",
                "labels": { "kubernetes.io/service-name": "web" }
              },
              "addressType": "IPv6",
              "ports": [{ "port": 8080, "protocol": "TCP" }],
              "endpoints": [{
                "addresses": ["2001:db8::1"],
                "conditions": { "ready": false }
              }]
            }
            """));
        Assert.IsNotNull(slice);
        Assert.AreEqual("IPv6", slice!.AddressType);
        Assert.AreEqual("web", slice.ServiceName);
        Assert.AreEqual(false, slice.Endpoints[0].Ready);
        Assert.AreEqual("2001:db8::1", slice.Endpoints[0].Addresses[0]);
    }

    [TestMethod]
    public void HttpRoute_MethodAndHeaderMatch_EmitsUserRoute()
    {
        var b = Basic();
        var match = new HttpRouteMatch
        {
            Path = new HttpPathMatch { Type = "PathPrefix", Value = "/api" },
            Method = "PATCH",
            Headers = [new HttpHeaderMatch { Name = "x-trace", Value = "on", Type = "Exact" }],
        };
        b.Routes.Add(Route("r", "default", ["api.example"], Rule("web", 8080, match)));
        var result = b.Render();
        AssertValid(result);
        var route = result.Config.UserRoutes().Single();
        Assert.AreEqual("PATCH", route.Match.Method);
        Assert.IsNotNull(route.Match.Headers);
        Assert.AreEqual(1, route.Match.Headers!.Count);
    }

    [TestMethod]
    public void DataPlaneObservation_ConfirmsOnlyWhenAllPodsMatch()
    {
        const string hash = "wave24-hash";
        var none = DataPlaneObservation.None;
        Assert.IsFalse(none.Confirms(hash));

        var partial = new DataPlaneObservation([hash, null]);
        Assert.IsFalse(partial.Confirms(hash));

        var mixed = new DataPlaneObservation([hash, "other"]);
        Assert.IsFalse(mixed.Confirms(hash));

        var all = new DataPlaneObservation([hash, hash]);
        Assert.IsTrue(all.Confirms(hash));
    }

    [TestMethod]
    public void StatusPatches_Programmed_FlipsAfterFinalize()
    {
        var result = Basic().Render();
        const string hash = "wave24-hash";
        var pending = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Conditions!
            .Single(c => c.Type == "Programmed");
        Assert.AreEqual("False", pending.Status);

        var confirmed = result.Finalize(new DataPlaneObservation([hash]), hash);
        var programmed = confirmed.Single(p => p.Kind == StatusKind.Gateway).Conditions!
            .Single(c => c.Type == "Programmed");
        Assert.AreEqual("True", programmed.Status);
    }
}
