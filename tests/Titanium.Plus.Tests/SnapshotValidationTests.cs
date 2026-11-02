using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.ControlPlane;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
namespace Titanium.Plus.Tests;

[TestClass]
public class SnapshotValidationTests
{
    private const string Secret = "operator-secret-value";

    [TestMethod]
    public async Task SnapshotPut_Enforce_RejectsInvalidRouteWith422()
    {
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r1",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };
        var editor = new FakeEditor(["Route 'r1' references unknown cluster 'c1'."]);
        using var server = new ControlPlaneServer(
            new ClusterManager(),
            "127.0.0.1",
            0,
            Secret,
            routes,
            configEditor: editor,
            snapshotValidation: SnapshotValidationMode.Enforce);
        server.Start();
        using var http = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, server.Prefix + "v1/snapshot");
        put.Headers.Add(ControlPlaneServer.SharedSecretHeader, Secret);
        put.Content = new StringContent(
            """{"routes":[{"id":"r1","clusterId":"missing","match":{"path":"/","pathKind":"Prefix"}}]}""",
            Encoding.UTF8,
            "application/json");
        var response = await http.SendAsync(put);
        Assert.AreEqual((HttpStatusCode)422, response.StatusCode);
    }

    [TestMethod]
    public async Task SnapshotPut_Warn_AppliesAndSetsValidationHeader()
    {
        var routes = new List<RouteConfig>();
        var editor = new FakeEditor(["Route 'r1' references unknown cluster 'missing'."]);
        using var server = new ControlPlaneServer(
            new ClusterManager(),
            "127.0.0.1",
            0,
            Secret,
            routes,
            configEditor: editor,
            snapshotValidation: SnapshotValidationMode.Warn);
        server.Start();
        using var http = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, server.Prefix + "v1/snapshot");
        put.Headers.Add(ControlPlaneServer.SharedSecretHeader, Secret);
        put.Content = new StringContent(
            """{"routes":[{"id":"r1","clusterId":"missing","match":{"path":"/","pathKind":"Prefix"}}]}""",
            Encoding.UTF8,
            "application/json");
        var response = await http.SendAsync(put);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("failed", response.Headers.GetValues("X-Titanium-Validation").First());
    }

    private sealed class FakeEditor : IConfigEditor
    {
        private readonly IReadOnlyList<string> _errors;

        public FakeEditor(IReadOnlyList<string> errors) => _errors = errors;

        public IReadOnlyList<string> ValidateCandidate(IReadOnlyList<RouteConfig>? routes, IReadOnlyList<ClusterConfig>? clusters) =>
            _errors;

        public Task<ConfigEditResult> ApplyEditAsync(ConfigEdit edit, string actor, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ConfigEditResult> ClearOverlayAsync(string actor, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public OverlayStatus GetOverlayStatus() => new();
    }
}
