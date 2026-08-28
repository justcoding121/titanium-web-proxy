using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class DashboardHostTests
{
    [TestMethod]
    public async Task Dashboard_RequiresSecret_AndSupportsAdminRoutes()
    {
        Environment.SetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET", "1");
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

        var port = GetFreePort();
        var secret = "dashboard-test-secret";
        using var control = new ControlPlaneServer(manager, "127.0.0.1", port, secret);
        control.Start();
        var ops = new DrainOperations(manager);
        var metrics = new PrometheusMetricsExporter(manager, null);
        using var dash = new DashboardHost(control, ops, metrics, manager);
        dash.Start();
        Assert.IsNotNull(dash.Prefix);

        using var http = new HttpClient();

        var unauth = await http.GetAsync(dash.Prefix);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using var authReq = new HttpRequestMessage(HttpMethod.Get, dash.Prefix);
        authReq.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        var html = await http.SendAsync(authReq);
        Assert.AreEqual(HttpStatusCode.OK, html.StatusCode);
        var body = await html.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "Titanium Plus");
        StringAssert.Contains(body, "d1");

        using var metricsReq = new HttpRequestMessage(HttpMethod.Get, dash.Prefix + "metrics");
        metricsReq.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        var metricsResp = await http.SendAsync(metricsReq);
        Assert.AreEqual(HttpStatusCode.OK, metricsResp.StatusCode);
        StringAssert.Contains(await metricsResp.Content.ReadAsStringAsync(), "titanium_destination_state");

        using var snapReq = new HttpRequestMessage(HttpMethod.Get, dash.Prefix + "api/snapshot");
        snapReq.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        var snapResp = await http.SendAsync(snapReq);
        Assert.AreEqual(HttpStatusCode.OK, snapResp.StatusCode);
        StringAssert.Contains(await snapResp.Content.ReadAsStringAsync(), "d1");

        using var drainReq = new HttpRequestMessage(HttpMethod.Post, dash.Prefix + "drain/d1");
        drainReq.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(drainReq)).StatusCode);
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));

        using var healthyReq = new HttpRequestMessage(HttpMethod.Post, dash.Prefix + "healthy/d1");
        healthyReq.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(healthyReq)).StatusCode);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));

        using var drainUnauth = new HttpRequestMessage(HttpMethod.Post, dash.Prefix + "drain/d1");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(drainUnauth)).StatusCode);
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
