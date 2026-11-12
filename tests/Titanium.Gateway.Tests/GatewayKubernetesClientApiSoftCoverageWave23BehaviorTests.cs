using System.Net;
using System.Text;
using k8s;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: KubernetesClientApi PUT without body and dispose after SendAsync.
/// </summary>
[TestClass]
public class GatewayKubernetesClientApiSoftCoverageWave23BehaviorTests
{
    [TestMethod]
    public async Task SendAsync_PutWithoutBody_ReturnsStatus()
    {
        var handler = new StubKubeHandler();
        using var api = CreateApi(handler);

        var put = await api.SendAsync(
            HttpMethod.Put,
            "/apis/coordination.k8s.io/v1/namespaces/default/leases/gw",
            body: null,
            contentType: null,
            CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, put.Status);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsNull(handler.Requests[0].Content);
    }

    [TestMethod]
    public void Dispose_IsIdempotent_AfterSend()
    {
        var handler = new StubKubeHandler();
        var api = CreateApi(handler);
        _ = api.SendAsync(HttpMethod.Get, "/version", null, null, CancellationToken.None).GetAwaiter().GetResult();
        api.Dispose();
        api.Dispose();
        Assert.AreEqual(1, handler.Requests.Count);
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
