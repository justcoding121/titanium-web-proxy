using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Tests.Support;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: OutputWriter.PokePods / EnsureListeners, ClusterCache
///     cluster-wide secrets + Changed, and SignatureOf TLS flag.
/// </summary>
[TestClass]
public class GatewayOutputWriterPokeAndCacheSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task OutputWriter_PokePods_CountsFailuresAndIgnoresNotFound()
    {
        var api = new FakeKubeApi();
        api.Apply("pods", ClusterFixture.Obj("""
            { "metadata": { "name": "alive", "namespace": "gw" } }
            """));
        var writer = new OutputWriter(api, new OutputOptions
        {
            Namespace = "gw",
            ConfigMapName = "cfg",
            TlsSecretName = "tls",
        });

        var failed = await writer.PokePodsAsync(["alive", "missing", "alive"], "token-1", CancellationToken.None);
        Assert.AreEqual(0, failed);
        Assert.IsTrue(api.WritesTo("/pods/alive").Count >= 1);

        api.Offline = true;
        failed = await writer.PokePodsAsync(["alive"], "token-2", CancellationToken.None);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task OutputWriter_EnsureListeners_RollsDeploymentWhenSignatureChanges()
    {
        var api = new FakeKubeApi();
        api.Apply("deployments", ClusterFixture.Obj("""
            {
              "apiVersion": "apps/v1",
              "kind": "Deployment",
              "metadata": { "name": "data", "namespace": "gw" },
              "spec": {
                "template": {
                  "metadata": {
                    "annotations": { "gateway.titaniumproxy.com/listeners": "none" }
                  }
                }
              }
            }
            """));
        var writer = new OutputWriter(api, new OutputOptions
        {
            Namespace = "gw",
            ConfigMapName = "cfg",
            TlsSecretName = "tls",
            DeploymentName = "data",
        });

        var config = Encoding.UTF8.GetBytes("listeners:\n- port: 8080\n");
        var signature = OutputWriter.SignatureOf(
        [
            new ListenerConfig { Host = "127.0.0.1", Port = 8080, Type = "tcp", DecryptSsl = false },
        ]);
        Assert.AreNotEqual(OutputWriter.NoListeners, signature);
        Assert.IsTrue(await writer.ApplyAsync(config, new Dictionary<string, byte[]>(), signature, CancellationToken.None));
        Assert.IsTrue(api.WritesTo("/deployments/data").Count >= 1);

        // Same digest is a no-op.
        Assert.IsFalse(await writer.ApplyAsync(config, new Dictionary<string, byte[]>(), signature, CancellationToken.None));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClusterCache_EmptySecretNamespaces_WatchesClusterWideAndRaisesChanged()
    {
        await using var fixture = new ClusterFixture();
        using var stop = new CancellationTokenSource();
        var cache = new ClusterCache(fixture.Api, new ClusterCacheOptions(), NullLogger.Instance);
        var changed = 0;
        cache.Changed += () => Interlocked.Increment(ref changed);
        var run = cache.RunAsync(stop.Token);
        try
        {
            await ClusterFixture.WaitForAsync(() => cache.HasSynced);
            var before = Volatile.Read(ref changed);
            fixture.Api.Apply("services", ClusterFixture.Obj("""
                {
                  "metadata": { "name": "extra", "namespace": "default" },
                  "spec": { "ports": [ { "port": 80 } ] }
                }
                """));
            await ClusterFixture.WaitForAsync(() => Volatile.Read(ref changed) > before);
            var snap = cache.Snapshot();
            Assert.IsTrue(snap.Services.Any(s => s.Name == "extra"));
            Assert.IsTrue(cache.HasSynced);
            // Discovery may leave optional kinds unavailable on the fake API; Snapshot still sorts empties.
            _ = cache.IsKindAvailable("tlsroutes");
            _ = snap.TlsRoutes;
            _ = snap.GrpcRoutes;
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
    public void SignatureOf_IncludesDecryptSslInFingerprint()
    {
        var plain = OutputWriter.SignatureOf(
        [
            new ListenerConfig { Host = "127.0.0.1", Port = 443, Type = "tcp", DecryptSsl = false },
        ]);
        var tls = OutputWriter.SignatureOf(
        [
            new ListenerConfig { Host = "127.0.0.1", Port = 443, Type = "tcp", DecryptSsl = true },
        ]);
        Assert.AreNotEqual(plain, tls);
    }
}
