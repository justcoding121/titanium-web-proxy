using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Tests.Support;

namespace Titanium.Gateway.Tests;

/// <summary>
///     ClusterCache wiring for secret namespace scoping and BackendTLSPolicy ConfigMap watches.
///     Bug classes: cluster-wide secret watch when namespaces are listed, ConfigMaps watched with the flag off.
/// </summary>
[TestClass]
public class ClusterCacheBehaviorTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task SecretNamespaces_AndBackendTlsFlag_ShapeSnapshot()
    {
        await using var fixture = new ClusterFixture();
        fixture.Api.Apply("configmaps", ClusterFixture.Obj("""
            {
              "metadata": { "name": "backend-ca", "namespace": "default" },
              "data": { "ca.crt": "pem-bytes" }
            }
            """));
        fixture.Api.Apply("backendtlspolicies", ClusterFixture.Obj("""
            {
              "metadata": { "name": "web-tls", "namespace": "default" },
              "spec": {
                "targetRefs": [ { "kind": "Service", "name": "web" } ],
                "validation": {
                  "hostname": "web.svc.cluster.local",
                  "wellKnownCACertificates": "System"
                }
              }
            }
            """));

        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(
            fixture.Api,
            new ClusterCacheOptions
            {
                SecretNamespaces = ["default"],
                BackendTlsPolicyEnabled = true,
            },
            NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var snap = cache.Snapshot();
            Assert.IsTrue(snap.BackendTlsPolicies.Any(p => p.Metadata.Name == "web-tls"));
            Assert.IsTrue(snap.ConfigMaps.Any(c => c.Name == "backend-ca" && c.Data["ca.crt"] == "pem-bytes"));
            Assert.IsTrue(snap.Secrets.All(s => s.Namespace == "default"));
            Assert.IsTrue(cache.IsKindAvailable("httproutes") || cache.IsKindAvailable("backendtlspolicies"));
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task BackendTlsDisabled_OmitsConfigMapsFromSnapshot()
    {
        await using var fixture = new ClusterFixture();
        fixture.Api.Apply("configmaps", ClusterFixture.Obj("""
            {
              "metadata": { "name": "backend-ca", "namespace": "default" },
              "data": { "ca.crt": "pem" }
            }
            """));

        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(
            fixture.Api,
            new ClusterCacheOptions { BackendTlsPolicyEnabled = false },
            NullLogger.Instance);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var snap = cache.Snapshot();
            Assert.AreEqual(0, snap.ConfigMaps.Count);
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
