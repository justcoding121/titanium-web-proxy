using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: HealthServer bind failure, BackendTls non-Service targetRef skip,
///     gRPC route with no parentRefs, and dual-wildcard hostname intersect.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave19BehaviorTests
{
    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    [TestMethod]
    [Timeout(15_000)]
    public async Task HealthServer_LoopbackPortInUse_ThrowsOnRun()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = new HealthServer(port, () => true, NullLogger.Instance, prefix);
        await Assert.ThrowsExactlyAsync<HttpListenerException>(async () =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await server.RunAsync(stop.Token);
        });
    }

    [TestMethod]
    public void BackendTls_NonServiceTargetRef_DoesNotApplyHttps()
    {
        var b = Basic();
        var policy = BackendTls("pol", "default", "web", "web.svc", wellKnownCa: "System");
        policy.Spec.TargetRefs =
        [
            new BackendTlsTargetRef { Kind = "Deployment", Name = "web", Group = "apps" },
        ];
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void GrpcRoute_EmptyParentRefs_ProducesNoGrpcStatus()
    {
        var b = Basic();
        var route = GrpcRoute("orphan", "default", null, GrpcRule("web"));
        route.Spec.ParentRefs = [];
        b.GrpcRoutes.Add(route);
        var result = b.Render();
        AssertValid(result);
        Assert.IsFalse(result.StatusPatches.Any(p => p.Kind == StatusKind.GrpcRoute));
        Assert.AreEqual(0, result.Config.UserRoutes().Count());
    }

    [TestMethod]
    public void Hostnames_DualWildcard_IntersectMoreSpecific()
    {
        Assert.IsTrue(Hostnames.TryIntersect("*.api.example", "*.example", out var nested));
        Assert.AreEqual("*.api.example", nested);
        Assert.IsTrue(Hostnames.TryIntersect("*.z.example", "*.example", out var z));
        Assert.AreEqual("*.z.example", z);
        Assert.IsFalse(Hostnames.TryIntersect("*.a.example", "*.b.example", out _));
    }
}
