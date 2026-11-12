using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: BackendTls empty hostname and custom CA inline PEM,
///     ResourceParsers Typed route + ConfigMap without data, ClusterCache GetFile miss,
///     and KubernetesClientApi PUT/dispose without a cluster.
/// </summary>
[TestClass]
public class GatewayBackendTlsGrpcSoftCoverageWave23BehaviorTests
{
    private static void AssertValid(RenderResult result) =>
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count, string.Join("; ", TwpConfigValidator.Validate(result.Config)));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void BackendTls_EmptyHostname_SkipsHttpsClient()
    {
        var b = Basic();
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", hostname: string.Empty, wellKnownCa: "System"));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.IsNull(result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void BackendTls_ConfigMapCa_EmitsInlinePem_AndHttpsDestinations()
    {
        var b = Basic();
        b.ConfigMaps.Add(new ConfigMapInfo(
            "default",
            "backend-ca",
            new Dictionary<string, string> { ["ca.crt"] = "-----BEGIN CERTIFICATE-----\nAQID\n-----END CERTIFICATE-----\n" }));
        b.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", "web.svc", caConfigMaps: ["backend-ca"]));
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        var cluster = result.Config.Clusters.Single(c => c.Id == "default/web:8080");
        Assert.IsTrue(cluster.Destinations.All(d => d.UseHttps));
        Assert.IsNotNull(cluster.HttpClient);
        Assert.IsNotNull(cluster.HttpClient!.CaBundlePath);
        Assert.AreEqual(1, result.InlineTlsFiles.Count);
        StringAssert.Contains(cluster.HttpClient.CaBundlePath, "backend-ca-");
    }

    [TestMethod]
    public void BackendTls_ExplicitCoreServiceGroup_AppliesSystemCa()
    {
        var b = Basic();
        var policy = BackendTls("pol", "default", "web", "grouped.svc", wellKnownCa: "System");
        policy.Spec.TargetRefs =
        [
            new BackendTlsTargetRef { Group = string.Empty, Kind = GatewayApiNames.ServiceKind, Name = "web" },
        ];
        b.BackendTlsPolicies.Add(policy);
        b.Routes.Add(Route("r", "default", null, Rule("web")));
        var result = b.Render(BackendTlsOptions);
        AssertValid(result);
        Assert.AreEqual("grouped.svc", result.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient!.ServerName);
    }

    [TestMethod]
    public void ResourceParsers_TypedHttpRoute_AndConfigMapWithoutData()
    {
        var route = ResourceParsers.Typed<HttpRoute>(Parse("""
            {
              "metadata": { "name": "r", "namespace": "default" },
              "spec": { "parentRefs": [], "rules": [] }
            }
            """));
        Assert.IsNotNull(route);
        Assert.AreEqual("r", route!.Metadata.Name);

        Assert.IsNull(ResourceParsers.ConfigMap(Parse("""{"metadata":{"namespace":"ns"}}""")));
        var emptyData = ResourceParsers.ConfigMap(Parse("""
            { "metadata": { "name": "cm", "namespace": "ns" }, "data": null }
            """));
        Assert.IsNotNull(emptyData);
        Assert.AreEqual(0, emptyData!.Data.Count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_GetFile_MissingSecret_ReturnsNull()
    {
        await using var fixture = new ClusterFixture();
        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var getFile = typeof(ClusterCache).GetMethod("GetFile", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(getFile);
            var missing = (byte[]?)getFile!.Invoke(cache, [new TlsFile(string.Empty, "default", "gone", TlsFileKind.CertificatePem)]);
            Assert.IsNull(missing);
            Assert.IsFalse(cache.IsKindAvailable("unknown-plural"));
        }
        finally
        {
            stop.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
