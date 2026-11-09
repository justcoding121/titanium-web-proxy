using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Fat leftover paths in TranslationPass.Rules (endpoint merge / zone-aware) and OutputWriter.ApplyAsync.
/// </summary>
[TestClass]
public sealed class ClusterEndpointsAndOutputWriterApplyBehaviorTests
{
    [TestMethod]
    public void ReadyEndpoints_SkipsUnknownAddressTypes_Dedups_AndKeepsZones()
    {
        var service = new ServiceInfo(
            "default",
            "web",
            "ClusterIP",
            "10.96.1.1",
            [new ServicePortInfo(null, 8080, 8080, null)],
            [],
            NoServiceAnnotations);
        var state = new ClusterState
        {
            Services = [service],
            EndpointSlices =
            [
                new EndpointSliceInfo(
                    "default",
                    "web-fqdn",
                    "web",
                    "FQDN",
                    [new EndpointPortInfo(null, 8080, "TCP")],
                    [new EndpointInfo(["pod.svc.cluster.local"], true, Zone: "z0")]),
                new EndpointSliceInfo(
                    "default",
                    "web-a",
                    "web",
                    "IPv4",
                    [new EndpointPortInfo(null, 8080, "TCP")],
                    [
                        new EndpointInfo(["10.0.0.1"], true, Zone: "us-east-1a"),
                        new EndpointInfo(["10.0.0.1"], true, Zone: "us-east-1b"),
                        new EndpointInfo(["10.0.0.2"], false),
                    ]),
                new EndpointSliceInfo(
                    "default",
                    "web-udp",
                    "web",
                    "IPv4",
                    [new EndpointPortInfo(null, 8080, "UDP")],
                    [new EndpointInfo(["10.0.0.9"], true)]),
            ],
        };

        var endpoints = new ClusterEndpoints(state).ReadyEndpoints(service, service.Ports[0]);
        CollectionAssert.AreEqual(new[] { "10.0.0.1" }, endpoints.Select(e => e.Address).ToArray());
        Assert.AreEqual("us-east-1a", endpoints.Single().Zone);
        var udp = new ClusterEndpoints(state).ReadyEndpoints(service, service.Ports[0], "UDP");
        Assert.AreEqual("10.0.0.9", udp.Single().Address);
    }

    [TestMethod]
    public void IsZoneAwareService_RequiresTrueAnnotation()
    {
        Assert.IsFalse(ClusterEndpoints.IsZoneAwareService(new ServiceInfo(
            "ns", "s", null, null, [], [], NoServiceAnnotations)));
        Assert.IsFalse(ClusterEndpoints.IsZoneAwareService(new ServiceInfo(
            "ns", "s", null, null, [], [],
            new Dictionary<string, string> { ["titanium.io/zone-aware"] = "yes" })));
        Assert.IsTrue(ClusterEndpoints.IsZoneAwareService(new ServiceInfo(
            "ns", "s", null, null, [], [],
            new Dictionary<string, string> { ["titanium.io/zone-aware"] = "TRUE" })));
    }

    [TestMethod]
    public void Translator_PropagatesZoneAwareAndEndpointZones()
    {
        var b = Basic();
        b.Services[0] = b.Services[0] with
        {
            Annotations = new Dictionary<string, string> { ["titanium.io/zone-aware"] = "true" },
        };
        b.Slices[0] = b.Slices[0] with
        {
            Endpoints = [new EndpointInfo(["10.0.0.1"], true, Zone: "zone-a")],
        };
        b.Routes.Add(Route("r", "default", null, new HttpRouteRule
        {
            BackendRefs = [new HttpBackendRef { Name = "web", Port = 8080 }],
        }));

        var cluster = b.Render().Config.Clusters.Single();
        Assert.IsNotNull(cluster.ZoneAware);
        Assert.AreEqual("zone-a", cluster.Destinations.Single().Zone);
    }

    [TestMethod]
    public async Task OutputWriter_RejectsOversizedConfig_AndSkipsIdenticalDigestUntilInvalidate()
    {
        var api = new FakeKubeApi();
        var writer = new OutputWriter(api, new OutputOptions
        {
            Namespace = "gw",
            ConfigMapName = "titanium-config",
            TlsSecretName = "titanium-tls",
        });
        var emptyTls = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            writer.ApplyAsync(new byte[OutputWriter.MaxConfigBytes + 1], emptyTls, OutputWriter.NoListeners, CancellationToken.None));

        var config = Encoding.UTF8.GetBytes("listeners: []\n");
        Assert.IsTrue(await writer.ApplyAsync(config, emptyTls, OutputWriter.NoListeners, CancellationToken.None));
        Assert.IsFalse(await writer.ApplyAsync(config, emptyTls, OutputWriter.NoListeners, CancellationToken.None));
        writer.Invalidate();
        Assert.IsTrue(await writer.ApplyAsync(config, emptyTls, OutputWriter.NoListeners, CancellationToken.None));
        Assert.IsTrue(api.WritesTo("/configmaps/").Count >= 2);
        Assert.IsTrue(api.WritesTo("/secrets/").Count >= 2);
    }
}
