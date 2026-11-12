using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector leftovers toward 85%: SessionBodyDiskCache stats/abandon/generation,
///     SystemProxyPacHelper, InspectorLogFiles path edges, and ProgressiveDelivery paced write with charge.
/// </summary>
[TestClass]
public class InspectorDiskCachePacAndLogSoftCoverageBehaviorTests
{
    [TestMethod]
    public void SessionBodyDiskCache_StatsAbandonGenerationAndDeleteEdges()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-disk-soft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 2_000_000, maxAge: TimeSpan.FromDays(1));
            var snap = new SessionSnapshot
            {
                Id = 3,
                Method = "POST",
                Url = "https://soft.example/",
                RequestBodyText = "req",
                ResponseBodyText = "resp",
                StatusCode = 201,
            };
            cache.Write(snap);
            Assert.IsTrue(cache.FileExists(3));
            Assert.IsTrue(cache.TryOpenRead(3, out var stream));
            Assert.IsNotNull(stream);
            stream!.Dispose();

            var stats = cache.GetCacheStats();
            Assert.IsTrue(stats.RunCount >= 1);
            Assert.IsTrue(stats.TotalBytes > 0);

            var orphan = Path.Combine(root, "extra-run");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "9.har"), "{}");
            var stats2 = cache.GetCacheStats();
            Assert.IsTrue(stats2.RunCount >= 2);

            var rejected = cache.Write(snap, expectedGeneration: cache.CurrentGeneration + 99);
            Assert.AreEqual(0, rejected.Count);

            cache.Delete(999);
            cache.DeleteMany([3, 999]);
            Assert.IsFalse(cache.FileExists(3));

            cache.Write(snap);
            var freed = cache.AbandonCurrentRun();
            Assert.IsTrue(freed >= 0);
            Assert.IsFalse(cache.FileExists(3));

            var goneRoot = Path.Combine(Path.GetTempPath(), "twp-disk-gone2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(goneRoot);
            using (var empty = new SessionBodyDiskCache(goneRoot, maxBytes: 1000, maxAge: TimeSpan.FromHours(1)))
            {
                Directory.Delete(goneRoot, recursive: true);
                var emptyStats = empty.GetCacheStats();
                Assert.AreEqual(0, emptyStats.RunCount);
                empty.ClearAllRuns();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void SystemProxyPacAndInspectorLogFiles_SoftEdges()
    {
        _ = SystemProxyPacHelper.HasActivePacScript();
        Assert.IsFalse(SystemProxyPacHelper.ScutilIndicatesPacOrWpad("ProxyAutoConfigEnable: 0"));
        Assert.IsTrue(SystemProxyPacHelper.ScutilIndicatesPacOrWpad("ProxyAutoConfigEnable: yes"));

        Assert.IsNull(InspectorLogFiles.MigrateFilePathToFolder(null));
        Assert.AreEqual("   ", InspectorLogFiles.MigrateFilePathToFolder("   "));
        Assert.AreEqual(
            Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "a.log")),
            InspectorLogFiles.MigrateFilePathToFolder(Path.Combine(Path.GetTempPath(), "a.log")));
        Assert.AreEqual("folder", InspectorLogFiles.MigrateFilePathToFolder("folder"));
        Assert.AreEqual(".log", InspectorLogFiles.MigrateFilePathToFolder(".log"));
        Assert.IsFalse(InspectorLogFiles.IsInspectorLogFileName(""));
        Assert.IsTrue(InspectorLogFiles.IsInspectorLogFileName("titanium-inspector.log"));
        Assert.IsTrue(InspectorLogFiles.IsInspectorLogFileName("titanium-inspector.log.3"));
        Assert.IsNull(OsUiLanguage.NormalizeTag("POSIX"));
        Assert.IsNotNull(OsUiLanguage.Detect());
        _ = OsUiLanguage.Capture();
        Assert.IsNull(MacPreferredLanguage.TryGetFirst());
    }

    [TestMethod]
    public async Task ProgressiveDelivery_WritePaced_ChargesAndSlices()
    {
        var plain = Encoding.UTF8.GetBytes(new string('z', ProgressiveDelivery.MinWireBytes + 250));
        var response = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = plain,
        };
        response.Headers.AddHeader(new HttpHeader("Content-Length", plain.Length.ToString()));
        var plan = ProgressiveDelivery.TryBuild(
            response, new Request { Method = "GET" }, plain, plain.Length, false, false);
        Assert.IsNotNull(plan);

        await using var output = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(
            output,
            plan!.Wire,
            chargeWireBytes: plan.Wire.Length,
            () => null,
            CancellationToken.None,
            sliceBytes: 64);
        CollectionAssert.AreEqual(plan.Wire, output.ToArray());

        await ProgressiveDelivery.WritePacedAsync(
            Stream.Null, Array.Empty<byte>(), chargeWireBytes: 0, () => null, CancellationToken.None);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
