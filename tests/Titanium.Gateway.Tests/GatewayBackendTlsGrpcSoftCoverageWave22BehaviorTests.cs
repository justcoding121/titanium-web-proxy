using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: Hostnames null/empty intersect, Conditions true status,
///     gRPC parent port match, and ResourceParsers endpoint/secret edges.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave22BehaviorTests
{
    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void Hostnames_TryIntersect_NullOrEmpty_UsesOtherSide()
    {
        Assert.IsTrue(Hostnames.TryIntersect(null, "APP.Example", out var fromNull));
        Assert.AreEqual("app.example", fromNull);
        Assert.IsTrue(Hostnames.TryIntersect("api.example", null, out var fromEmpty));
        Assert.AreEqual("api.example", fromEmpty);
        Assert.IsNull(Hostnames.Normalize("*"));
        Assert.IsNull(Hostnames.Normalize(""));
        Assert.AreEqual(2, Hostnames.Rank(null));
        Assert.AreEqual(0, Hostnames.Rank("exact.example"));
    }

    [TestMethod]
    public void Conditions_Make_RecordsTrueStatus()
    {
        var ok = Conditions.Make(Conditions.Programmed, true, "Programmed", "route", 7);
        Assert.AreEqual("True", ok.Status);
        Assert.AreEqual(Conditions.Programmed, ok.Type);
        Assert.AreEqual(7, ok.ObservedGeneration);
    }

    [TestMethod]
    public void GrpcRoute_ParentRefPort_MatchesHttpListener()
    {
        var b = Basic();
        var route = GrpcRoute("r", "default", ["grpc.example"], GrpcRule("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "gw", Port = 80 }];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);
        Assert.AreEqual(1, result.Config.UserRoutes().Count());
        Assert.AreEqual("True", result.StatusPatches.Single(p => p.Kind == StatusKind.GrpcRoute).Parents!.Single()
            .Conditions!.Single(c => c.Type == "Accepted").Status);
    }

    [TestMethod]
    public void ResourceParsers_EndpointSlicePodTargetAndSecretBadBase64()
    {
        static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        var slice = ResourceParsers.EndpointSlice(Parse("""
            {
              "metadata": { "name": "web-abc", "namespace": "default", "labels": { "kubernetes.io/service-name": "web" } },
              "addressType": "IPv4",
              "ports": [{ "name": "grpc", "port": 8080, "protocol": "TCP" }],
              "endpoints": [{
                "addresses": ["10.0.0.3"],
                "conditions": { "ready": true },
                "targetRef": { "kind": "Pod", "name": "web-pod-1" },
                "zone": "us-central1-a"
              }]
            }
            """));
        Assert.IsNotNull(slice);
        Assert.AreEqual("web-pod-1", slice!.Endpoints[0].PodName);
        Assert.AreEqual("us-central1-a", slice.Endpoints[0].Zone);

        var secret = ResourceParsers.Secret(Parse("""
            {
              "metadata": { "name": "tls", "namespace": "default", "resourceVersion": "99" },
              "type": "kubernetes.io/tls",
              "data": { "tls.crt": "not-valid-base64!!!", "tls.key": "", "ca.crt": null }
            }
            """));
        Assert.IsNotNull(secret);
        Assert.IsNull(secret!.Certificate);
        Assert.IsNull(secret.PrivateKey);
        Assert.IsNull(secret.CaCertificate);
    }
}
