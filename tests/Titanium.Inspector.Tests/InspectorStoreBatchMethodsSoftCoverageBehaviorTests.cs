using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore duplicate Add/AddMany/ClearAllSavedRuns,
///     MethodFilter empty and hide-tunnel edges, HostListFormat, InspectorBodyLimits brotli/deflate,
///     and LinkShaper zero timestamp frequency.
/// </summary>
[TestClass]
public class InspectorStoreBatchMethodsSoftCoverageBehaviorTests
{
    private static readonly string[] ExpectedHostListParse = ["*.example.com", "localhost"];

    [TestMethod]
    public void SessionStore_DuplicateAddAndAddMany_EnforcesLimitsWithoutSecondAddedEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = false, MaxSessionsInMemory = 2 },
                cacheDirectory: root);
            var added = 0;
            store.SessionAdded += _ => added++;

            store.Add(Snap(1));
            store.Add(Snap(2));
            Assert.AreEqual(2, added);

            store.Add(Snap(2));
            Assert.AreEqual(2, added);
            Assert.AreEqual(2, store.Sessions.Count);
            Assert.AreEqual("https://batch.example/2", store.TryGet(2)!.Url);

            store.AddMany([Snap(3), Snap(4), Snap(3)]);
            Assert.AreEqual(2, store.Sessions.Count);
            Assert.IsNull(store.TryGet(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void SessionStore_ClearAllSavedRunsWithoutDisk_AndRemoveRaisesRemoved()
    {
        var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        Assert.AreEqual(0, store.ClearAllSavedRuns());

        var removed = 0;
        store.SessionsRemoved += list => removed += list.Count;
        store.Add(Snap(10));
        store.Add(Snap(11));
        store.Remove([10]);
        Assert.AreEqual(1, removed);
        Assert.IsNull(store.TryGet(10));
    }

    [TestMethod]
    public void MethodFilter_EmptyMethodAndHideTunnel_AreHandled()
    {
        Assert.IsFalse(MethodFilter.IsShown("", "method:GET"));
        Assert.IsFalse(MethodFilter.Matches("GET", ",,"));
        Assert.IsFalse(MethodFilter.IsShown("CONNECT", "hide:tunnel"));
        Assert.IsFalse(MethodFilter.IsShown("CONNECT", "hide:connect"));

        var canon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MethodFilter.CollectMentioned("method:GET,,bad!", canon);
        CollectionAssert.Contains(canon.ToList(), "GET");
    }

    [TestMethod]
    public void HostListFormat_JoinNull_AndParseSkipsComments()
    {
        Assert.AreEqual("", HostListFormat.Join(null));
        var parsed = HostListFormat.Parse("# comment\r\n*.example.com\n\nlocalhost\n*.example.com\r");
        CollectionAssert.AreEquivalent(ExpectedHostListParse, parsed);
    }

    [TestMethod]
    public void InspectorBodyLimits_BrotliDeflateAndUnclosedHtml()
    {
        var plain = Encoding.UTF8.GetBytes("{\"a\":1}");
        using var gz = new MemoryStream();
        using (var brotli = new BrotliStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(plain);
        }

        var brotliDecoded = InspectorBodyLimits.DecodeCapturedPrefix(gz.ToArray(), "br");
        CollectionAssert.AreEqual(plain, brotliDecoded);

        using var z = new MemoryStream();
        using (var deflate = new DeflateStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(plain);
        }

        var deflateDecoded = InspectorBodyLimits.DecodeCapturedPrefix(z.ToArray(), "deflate");
        CollectionAssert.AreEqual(plain, deflateDecoded);

        var unclosed = InspectorBodyLimits.TryPrettyPrint("<html><body><p>hi", "text/html");
        Assert.IsNotNull(unclosed);
        StringAssert.Contains(unclosed, "hi");
    }

    [TestMethod]
    public void LinkShaper_ZeroTimestampFrequency_ReturnsZeroDelay()
    {
        var time = new ZeroFrequencyTime();
        using var shaper = new LinkShaper(NetworkThrottle.Slow3G, time, static (_, _) => Task.CompletedTask);
        Assert.AreEqual(TimeSpan.Zero, shaper.Reserve(ThrottleDirection.Upload, 50_000));
    }

    private static SessionSnapshot Snap(long id) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = "https://batch.example/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private sealed class ZeroFrequencyTime : TimeProvider
    {
        public override long GetTimestamp() => 0;

        public override long TimestampFrequency => 0;
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
        }
    }
}
