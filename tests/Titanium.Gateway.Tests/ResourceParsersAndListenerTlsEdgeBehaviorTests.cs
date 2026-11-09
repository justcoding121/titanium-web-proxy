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
///     Fat leftovers: ResourceParsers Namespace/EndpointSlice/ConfigMap and HTTPS cert-ref edges.
/// </summary>
[TestClass]
public sealed class ResourceParsersAndListenerTlsEdgeBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static ListenerStatus ListenerStatus(RenderResult result) =>
        result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single();

    [TestMethod]
    public void ResourceParsers_Namespace_EndpointSlice_AndConfigMap()
    {
        Assert.IsNull(ResourceParsers.Namespace(Parse("""{"metadata":{}}""")));
        var ns = ResourceParsers.Namespace(Parse("""{"metadata":{"name":"apps","labels":{"tier":"a","empty":null}}}"""));
        Assert.AreEqual("apps", ns!.Name);
        Assert.AreEqual("a", ns.Labels["tier"]);
        Assert.AreEqual(string.Empty, ns.Labels["empty"]);

        Assert.IsNull(ResourceParsers.EndpointSlice(Parse("""{"metadata":{"name":"s","namespace":"ns"}}""")));
        var slice = ResourceParsers.EndpointSlice(Parse("""
            {
              "metadata":{"name":"web-abc","namespace":"ns","labels":{"kubernetes.io/service-name":"web"}},
              "addressType":"IPv4",
              "ports":[{"name":"http","port":8080,"protocol":"TCP"},{"name":"bad"}],
              "endpoints":[
                {"addresses":["10.0.0.1",""],"conditions":{"ready":true},"zone":"z1","targetRef":{"kind":"Pod","name":"web-0"}},
                {"addresses":["10.0.0.2"],"conditions":{"ready":false}}
              ]
            }
            """));
        Assert.IsNotNull(slice);
        Assert.AreEqual("web", slice!.ServiceName);
        Assert.AreEqual(1, slice.Ports.Count);
        Assert.AreEqual("web-0", slice.Endpoints[0].PodName);
        Assert.AreEqual("z1", slice.Endpoints[0].Zone);
        Assert.AreEqual(1, slice.Endpoints[0].Addresses.Count);

        Assert.IsNull(ResourceParsers.ConfigMap(Parse("""{"metadata":{"name":"cm"}}""")));
        var map = ResourceParsers.ConfigMap(Parse("""{"metadata":{"name":"cm","namespace":"ns"},"data":{"ca.crt":"PEM","empty":null}}"""));
        Assert.AreEqual("PEM", map!.Data["ca.crt"]);
        Assert.AreEqual(string.Empty, map.Data["empty"]);
    }

    [TestMethod]
    public void Https_MissingCertificateRefs_AndNonSecretKind_AreRejected()
    {
        var missing = Basic();
        var noRefs = Https("https", 443, "app.example.com", "cert");
        noRefs.Tls!.CertificateRefs = [];
        missing.Gateways[0].Spec.Listeners = [noRefs];
        var missingResult = missing.Render();
        Assert.AreEqual(0, missingResult.Config.Listeners.Count);
        Assert.AreEqual("InvalidCertificateRef", Cond(ListenerStatus(missingResult).Conditions, "ResolvedRefs").Reason);

        var badKind = Basic();
        var listener = Https("https", 443, null, "cert");
        listener.Tls!.CertificateRefs = [new SecretObjectReference { Name = "cert", Kind = "ConfigMap", Group = "" }];
        badKind.Gateways[0].Spec.Listeners = [listener];
        badKind.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var badKindResult = badKind.Render();
        Assert.AreEqual(0, badKindResult.Config.Listeners.Count);
        Assert.AreEqual("InvalidCertificateRef", Cond(ListenerStatus(badKindResult).Conditions, "ResolvedRefs").Reason);

        var badGroup = Basic();
        var groupListener = Https("https", 443, null, "cert");
        groupListener.Tls!.CertificateRefs =
            [new SecretObjectReference { Name = "cert", Kind = "Secret", Group = "example.com" }];
        badGroup.Gateways[0].Spec.Listeners = [groupListener];
        badGroup.Secrets.Add(new SecretInfo("default", "cert", "kubernetes.io/tls", true, true));
        var badGroupResult = badGroup.Render();
        Assert.AreEqual(0, badGroupResult.Config.Listeners.Count);
        StringAssert.Contains(Cond(ListenerStatus(badGroupResult).Conditions, "ResolvedRefs").Message, "core Secret");
    }

    [TestMethod]
    public void GrpcRoute_UnsupportedMethodType_AndMethodOnlyExact_AreHandled()
    {
        var unsupported = Basic();
        unsupported.GrpcRoutes.Add(GrpcRoute(
            "bad",
            "default",
            null,
            new GrpcRouteRule
            {
                BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Type = "Prefix", Service = "svc", Method = "m" } }],
            }));
        var unsupportedResult = unsupported.Render();
        Assert.AreEqual(500, unsupportedResult.Config.UserRoutes().Single().DirectResponse?.Status);

        var methodOnly = Basic();
        methodOnly.GrpcRoutes.Add(GrpcRoute(
            "mo",
            "default",
            null,
            new GrpcRouteRule
            {
                BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Method = "SayHello" } }],
            }));
        var methodOnlyResult = methodOnly.Render();
        var match = methodOnlyResult.Config.UserRoutes().Single().Match!;
        Assert.AreEqual(PathMatchKind.Regex, match.PathKind);
        StringAssert.Contains(match.Path!, "SayHello");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
