using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class PlusModuleTests
{
    [TestMethod]
    public void RequiredAbstractionsVersion_Is70()
    {
        ITitaniumPlusModule module = new TitaniumPlusModule();
        Assert.AreEqual(new Version(7, 0, 0), module.RequiredAbstractionsVersion);
    }

    [TestMethod]
    public async Task DrainOperations_SetsDestinationState()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var ops = new DrainOperations(manager);
        ops.Drain("d1");
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task PrometheusExporter_RendersDestinationGauge()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var text = new PrometheusMetricsExporter(manager, null).Render();
        StringAssert.Contains(text, "titanium_destination_state");
        StringAssert.Contains(text, "d1");
    }

    [TestMethod]
    public void ValidateSecret_RejectsChangeme_OnNonLoopback()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            ControlPlaneServer.ValidateSecret("0.0.0.0", "changeme"));
    }

    [TestMethod]
    public void ValidateSecret_AllowsChangeme_OnLoopbackWithDevFlag()
    {
        ControlPlaneServer.ValidateSecret("127.0.0.1", "changeme", allowInsecureDevSecret: true);
    }

    [TestMethod]
    public async Task ControlPlane_GetUnauthorized_WithoutSecret()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
            },
        ]);

        var port = GetFreePort();
        using var server = new ControlPlaneServer(manager, "127.0.0.1", port, "test-secret");
        server.Start();
        await Task.Delay(100);

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/v1/snapshot");
        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [TestMethod]
    public async Task ControlPlane_PutApply_UpdatesSnapshot()
    {
        var manager = new ClusterManager();
        var port = GetFreePort();
        using var server = new ControlPlaneServer(manager, "127.0.0.1", port, "test-secret");
        server.Start();
        await Task.Delay(100);

        var body = """
            [{"id":"c2","destinations":[{"id":"d2","address":"10.0.0.2","port":8080}]}]
            """;
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/v1/snapshot")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(ControlPlaneServer.SharedSecretHeader, "test-secret");
        var resp = await http.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("c2"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d2"));
    }

    [TestMethod]
    public void PlusInspectorPanels_HaveTitles()
    {
        var panels = new PlusInspectorViewProvider().CreatePanels(new InspectorPanelContext { HostWindow = new object() });
        Assert.IsTrue(panels.Count >= 2);
        Assert.IsTrue(panels.All(p => p is PlusInspectorPanel));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
