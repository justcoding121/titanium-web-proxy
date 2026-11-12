using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: BackendTls ConfigMap without ca.crt, gRPC dual ref messages,
///     zero-weight gRPC backends, missing gateway parent on TCP, UDP on HTTP-only Gateway,
///     and loopback HealthServer root path.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcL4SoftCoverageWaveBehaviorTests
{
    private static Condition Cond(IReadOnlyList<Condition> list, string type) => list.Single(c => c.Type == type);

    private static RouteParentStatus Parent(RenderResult result, StatusKind kind, string route = "r") =>
        result.StatusPatches.Single(p => p.Kind == kind && p.Name == route).Parents!.Single();

    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    public void BackendTls_ConfigMapWithoutCaCrtKey_SkipsTls()
    {
        var b = Basic();
        b.ConfigMaps.Add(new ConfigMapInfo("default", "ca", new Dictionary<string, string> { ["data"] = "ignored" }));
        b.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc",
            caConfigMaps: ["ca"]));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
        Assert.AreEqual(0, result.InlineTlsFiles.Count);
    }

    [TestMethod]
    public void GrpcRoute_TwoUnresolvedBackends_AccumulatesRefMessages()
    {
        var b = Basic();
        b.GrpcRoutes.Add(GrpcRoute(
            "r",
            "default",
            null,
            new GrpcRouteRule
            {
                BackendRefs =
                [
                    Backend("ghost-a"),
                    Backend("ghost-b"),
                ],
                Matches = [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }],
            }));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.GrpcRoute);
        var refs = Cond(parent.Conditions, "ResolvedRefs");
        Assert.AreEqual("False", refs.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(refs.Message));
        Assert.IsTrue(refs.Message!.Contains("ghost-a", StringComparison.Ordinal));
        Assert.IsTrue(refs.Message.Contains("ghost-b", StringComparison.Ordinal));
        var userRoute = result.Config.UserRoutes().Single();
        Assert.AreEqual(500, userRoute.DirectResponse?.Status);
    }

    [TestMethod]
    public void GrpcRoute_AllZeroWeightBackends_Direct500_NoUserRoute()
    {
        var b = Basic();
        var rule = GrpcRule(
            "web",
            matches: [new GrpcRouteMatch { Method = new GrpcMethodMatch { Service = "pkg.Echo", Method = "Say" } }]);
        rule.BackendRefs =
        [
            new HttpBackendRef { Name = "web", Port = 8080, Weight = 0 },
        ];
        b.GrpcRoutes.Add(GrpcRoute("r", "default", null, rule));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.GrpcRoute);
        Assert.AreEqual("True", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual(500, result.Config.UserRoutes().Single().DirectResponse?.Status);
    }

    [TestMethod]
    public void TcpRoute_MissingGatewayParent_NoStatusPatch()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners = [Tcp("tcp", 80)];
        var route = TcpRoute("r", "default", Backend("web"));
        route.Spec.ParentRefs = [new ParentReference { Name = "missing-gw", Namespace = "default" }];
        b.TcpRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);
        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.TcpRoute));
    }

    [TestMethod]
    public void UdpRoute_OnHttpOnlyGateway_NoMatchingParent()
    {
        var b = Basic();
        b.AddUdpService("default", "dns", 53, "10.0.0.5");
        b.UdpRoutes.Add(UdpRoute("r", "default", Backend("dns", 53)));
        var result = b.Render();
        AssertValid(result);

        var parent = Parent(result, StatusKind.UdpRoute);
        Assert.AreEqual("False", Cond(parent.Conditions, "Accepted").Status);
        Assert.AreEqual("NoMatchingParent", Cond(parent.Conditions, "Accepted").Reason);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task HealthServer_RootPath_OnLoopback_ReturnsNotFound()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        using var server = new HealthServer(port, () => true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, prefix);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        try
        {
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(20);
                }
            }

            using var client = new HttpClient { BaseAddress = new Uri(prefix) };
            var resp = await client.GetAsync("/");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
            Assert.AreEqual("not found", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            stop.Cancel();
            try
            {
                await running;
            }
            catch (Exception)
            {
                // cancelled
            }
        }
    }
}
