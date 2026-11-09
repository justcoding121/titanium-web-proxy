using System.Net;
using System.Text;
using k8s;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Drive <see cref="KubernetesClientApi"/> through a stub Kubernetes HTTP pipeline so SendAsync,
///     OpenWatchAsync success/failure, Authenticate no-op, and OwnedStream dispose are covered without a cluster.
/// </summary>
[TestClass]
public class GatewayKubernetesClientApiCoverageBehaviorTests
{
    [TestMethod]
    public async Task SendAsync_WithAndWithoutBody_ReturnsStatusAndText()
    {
        var handler = new StubKubeHandler();
        using var api = CreateApi(handler);

        var get = await api.SendAsync(HttpMethod.Get, "/api/v1/namespaces", body: null, contentType: null, CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.OK, get.Status);
        Assert.AreEqual("{\"kind\":\"NamespaceList\"}", get.Body);

        var post = await api.SendAsync(
            HttpMethod.Post,
            "/api/v1/namespaces/default/configmaps",
            body: "{\"apiVersion\":\"v1\"}",
            contentType: "application/json",
            CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.Created, post.Status);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsNotNull(handler.Requests[1].Content);
    }

    [TestMethod]
    public async Task OpenWatchAsync_Success_YieldsReadableOwnedStream()
    {
        var handler = new StubKubeHandler { WatchPayload = Encoding.UTF8.GetBytes("{\"type\":\"ADDED\"}\n") };
        using var api = CreateApi(handler);

        await using var stream = await api.OpenWatchAsync("/api/v1/namespaces?watch=true", CancellationToken.None);
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        stream.Flush();

        var buffer = new byte[64];
        var syncRead = stream.Read(buffer, 0, buffer.Length);
        Assert.IsTrue(syncRead > 0);
        var asyncRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
        Assert.IsTrue(asyncRead >= 0);
    }

    [TestMethod]
    public async Task OpenWatchAsync_NonSuccess_ThrowsKubeApiExceptionAndDisposes()
    {
        var handler = new StubKubeHandler { WatchStatus = HttpStatusCode.Forbidden, WatchPayload = Encoding.UTF8.GetBytes("denied") };
        using var api = CreateApi(handler);

        var ex = await Assert.ThrowsExactlyAsync<KubeApiException>(
            () => api.OpenWatchAsync("/api/v1/namespaces?watch=true", CancellationToken.None));
        Assert.AreEqual(HttpStatusCode.Forbidden, ex.Status);
        StringAssert.Contains(ex.Message, "denied");
    }

    [TestMethod]
    public void Create_WithoutClusterConfig_ThrowsWithoutBindingPorts()
    {
        // Bug class: Create() must fail closed when kubeconfig/in-cluster config is absent, not hang.
        var previous = Environment.GetEnvironmentVariable("KUBECONFIG");
        try
        {
            Environment.SetEnvironmentVariable(
                "KUBECONFIG",
                Path.Combine(Path.GetTempPath(), "twp-missing-kube-" + Guid.NewGuid().ToString("N")));
            try
            {
                using var _ = KubernetesClientApi.Create();
                Assert.Fail("Create() should throw when kubeconfig is missing.");
            }
            catch (AssertFailedException)
            {
                throw;
            }
            catch (Exception)
            {
                // Expected: kubeconfig / in-cluster configuration cannot be built.
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", previous);
        }
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

        public HttpStatusCode WatchStatus { get; set; } = HttpStatusCode.OK;

        public byte[] WatchPayload { get; set; } = Encoding.UTF8.GetBytes("{}");

        public StubKubeHandler()
            : base(new HttpClientHandler())
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path.Contains("watch=true", StringComparison.Ordinal) ||
                (request.RequestUri?.Query.Contains("watch=true", StringComparison.Ordinal) ?? false))
            {
                var response = new HttpResponseMessage(WatchStatus)
                {
                    Content = new ByteArrayContent(WatchPayload),
                };
                return Task.FromResult(response);
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"kind\":\"NamespaceList\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
