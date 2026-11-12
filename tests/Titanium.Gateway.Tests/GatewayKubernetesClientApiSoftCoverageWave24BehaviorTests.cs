using System.Net;
using System.Text;
using k8s;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: KubernetesClientApi PATCH and DELETE without a cluster.
/// </summary>
[TestClass]
public class GatewayKubernetesClientApiSoftCoverageWave24BehaviorTests
{
    [TestMethod]
    public async Task SendAsync_PatchAndDelete_ReturnStatus()
    {
        var handler = new StubKubeHandler();
        using var api = CreateApi(handler);

        var patch = await api.SendAsync(
            HttpMethod.Patch,
            "/api/v1/namespaces/default/configmaps/gw-config",
            body: """{"metadata":{"labels":{"patched":"1"}}}""",
            contentType: "application/merge-patch+json",
            CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, patch.Status);

        var delete = await api.SendAsync(
            HttpMethod.Delete,
            "/api/v1/namespaces/default/events/wave24",
            body: null,
            contentType: null,
            CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, delete.Status);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Delete, handler.Requests[1].Method);
    }

    private static KubernetesClientApi CreateApi(StubKubeHandler handler)
    {
        var config = new KubernetesClientConfiguration
        {
            Host = "http://127.0.0.1:9",
            SkipTlsVerify = true,
        };
        var client = new Kubernetes(config, handler);
        return new KubernetesClientApi(client);
    }

    private sealed class StubKubeHandler : DelegatingHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public StubKubeHandler()
            : base(new HttpClientHandler())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
