using System.Net;
using System.Net.Sockets;
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
    private static readonly string[] DashboardLeakProbePaths = ["api/snapshot", "api/no-such", "routes", "%00"];

    private static readonly System.Text.RegularExpressions.Regex StackLeakPattern = new(
        @"at Titanium\.|StackTrace|System\.Exception",
        System.Text.RegularExpressions.RegexOptions.Compiled);

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

        var secret = "dashboard-test-secret";
        using var control = StartControlPlaneOrRetry(manager, secret);
        var ops = new DrainOperations(manager);
        var metrics = new PrometheusMetricsExporter(manager, null);
        // Dashboard binds its own ephemeral port (never controlPort+1); clients must use dash.Prefix.
        using var dash = new DashboardHost(control, ops, metrics, manager);
        dash.Start();
        Assert.IsNotNull(dash.Prefix);
        Assert.AreNotEqual(control.Port, dash.BoundPort);

        using var http = new HttpClient();

        var unauth = await http.GetAsync(dash.Prefix + "api/snapshot");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using var authReq = new HttpRequestMessage(HttpMethod.Get, dash.Prefix + "routes");
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

    [TestMethod]
    public async Task Dashboard_UnauthenticatedProbes_DoNotLeakAStack()
    {
        Environment.SetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET", "1");
        var manager = new ClusterManager();
        using var control = StartControlPlaneOrRetry(manager, "dashboard-test-secret");
        using var dash = new DashboardHost(control, new DrainOperations(manager), new PrometheusMetricsExporter(manager, null), manager);
        dash.Start();
        using var http = new HttpClient();
        foreach (var path in DashboardLeakProbePaths)
        {
            var response = await http.GetAsync(dash.Prefix + path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(
                StackLeakPattern.IsMatch(body),
                path + " leaked " + body);
            Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode, path);
        }
    }

    private static ControlPlaneServer StartControlPlaneOrRetry(
        IClusterManager manager, string secret, int maxAttempts = 8)
    {
        Exception? last = null;
        for (var i = 0; i < maxAttempts; i++)
        {
            var port = GetFreePort();
            var server = new ControlPlaneServer(manager, "127.0.0.1", port, secret);
            try
            {
                server.Start();
                return server;
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                server.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Failed to start ControlPlaneServer after {maxAttempts} attempts.", last);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
