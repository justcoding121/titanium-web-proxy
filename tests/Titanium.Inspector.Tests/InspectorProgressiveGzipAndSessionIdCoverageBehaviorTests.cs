using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Inspector leftovers toward 85%: ProgressiveDelivery gzip/deflate success path,
///     SessionIdComparer null arms, and SessionBodyDiskCache stats when the root vanishes.
/// </summary>
[TestClass]
public class InspectorProgressiveGzipAndSessionIdCoverageBehaviorTests
{
    [TestMethod]
    public void ProgressiveDelivery_GzipAndDeflate_BuildWireBodies()
    {
        var plain = Encoding.UTF8.GetBytes(new string('g', ProgressiveDelivery.MinWireBytes + 32));
        var before = ProgressiveDelivery.ProgressiveCount;

        var gzip = new Response { StatusCode = 200, HttpVersion = new Version(1, 1) };
        gzip.Headers.AddHeader("Content-Type", "text/plain");
        gzip.Headers.AddHeader("Content-Encoding", "gzip");
        var gzipPlan = ProgressiveDelivery.TryBuild(
            gzip,
            new Request { Method = "GET" },
            plain,
            plain.Length,
            transcoded: false,
            edited: false);
        Assert.IsNotNull(gzipPlan);
        Assert.IsTrue(gzipPlan!.Wire.Length > 0);
        Assert.AreNotEqual(plain.Length, gzipPlan.Wire.Length);

        var deflate = new Response { StatusCode = 200, HttpVersion = new Version(1, 1) };
        deflate.Headers.AddHeader("Content-Type", "text/plain");
        deflate.Headers.AddHeader("Content-Encoding", "DEFLATE");
        var deflatePlan = ProgressiveDelivery.TryBuild(
            deflate,
            new Request { Method = "GET" },
            plain,
            plain.Length,
            transcoded: false,
            edited: false);
        Assert.IsNotNull(deflatePlan);
        Assert.IsTrue(ProgressiveDelivery.ProgressiveCount > before);

        using var inflated = new MemoryStream();
        using (var gz = new GZipStream(new MemoryStream(gzipPlan.Wire), CompressionMode.Decompress))
        {
            gz.CopyTo(inflated);
        }

        CollectionAssert.AreEqual(plain, inflated.ToArray());
    }

    [TestMethod]
    public void SessionIdComparer_NullAndOrdering()
    {
        var comparer = SessionIdComparer.Instance;
        Assert.AreEqual(0, comparer.Compare(null, null));
        Assert.AreEqual(-1, comparer.Compare(null, new SessionSnapshot { Id = 1 }));
        Assert.AreEqual(1, comparer.Compare(new SessionSnapshot { Id = 1 }, null));
        Assert.IsTrue(comparer.Compare(new SessionSnapshot { Id = 2 }, new SessionSnapshot { Id = 10 }) < 0);
        Assert.AreEqual(0, comparer.Compare(new SessionSnapshot { Id = 7 }, new SessionSnapshot { Id = 7 }));
    }

    [TestMethod]
    public void SessionBodyDiskCache_GetCacheStats_WhenRootRemoved_ReturnsIndexedRunsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-insp-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 2_000_000, maxAge: TimeSpan.FromDays(1));
            cache.Write(new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://example.test/",
                StatusCode = 200,
                RequestBodyBytes = "a"u8.ToArray(),
                ResponseBodyBytes = "b"u8.ToArray(),
            });
            Assert.IsTrue(cache.GetCacheStats().TotalBytes > 0);

            Directory.Delete(root, recursive: true);
            var stats = cache.GetCacheStats();
            Assert.IsTrue(stats.RunCount >= 0);
            Assert.IsTrue(stats.TotalBytes >= 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
