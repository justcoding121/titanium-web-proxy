using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Inspector.Tests;

[TestClass]
public class HarTimingMapTests
{
    [TestMethod]
    public void Map_SumsToTotal_PutsSslInsideConnect_AndKeepsDurationMeaning()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timing = FirstUse(start, out var connection);
        var phases = HarTimingMap.Map(timing, connection);

        Assert.AreEqual(SessionDisplayFormat.RoundMs(timing.TotalDuration.TotalMilliseconds), HarTimingMap.Sum(phases), 0.05);
        Assert.AreEqual(SessionDisplayFormat.RoundMs(timing.TimeToFirstByte!.Value.TotalMilliseconds), phases.WaitMs, 0.05);
        Assert.IsTrue(phases.SslMs >= 0);
        Assert.IsTrue(phases.ConnectMs >= phases.SslMs);
        Assert.AreEqual(0, phases.BlockedMs + phases.DnsMs + phases.ConnectMs + phases.SendMs + phases.WaitMs + phases.ReceiveMs - HarTimingMap.Sum(phases), 0.05);
        Assert.IsFalse(phases.Reused);
    }

    [TestMethod]
    public void Map_ReusedAndPrefetchedConnections_OmitSetupPhases()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reused = FirstUse(start, out var connection);
        reused.UpstreamConnectionReused = true;
        var reusedPhases = HarTimingMap.Map(reused, connection);
        Assert.AreEqual(-1, reusedPhases.DnsMs);
        Assert.AreEqual(-1, reusedPhases.ConnectMs);
        Assert.AreEqual(-1, reusedPhases.SslMs);
        Assert.IsTrue(reusedPhases.Reused);
        Assert.AreEqual(SessionDisplayFormat.RoundMs(reused.TotalDuration.TotalMilliseconds), HarTimingMap.Sum(reusedPhases), 0.05);

        var prefetchedConnection = new UpstreamConnectionTiming(start.AddSeconds(-5));
        prefetchedConnection.DnsResolvedAt = start.AddSeconds(-4);
        prefetchedConnection.TcpConnectedAt = start.AddSeconds(-3);
        prefetchedConnection.EstablishedAt = start.AddSeconds(-3);
        var fresh = FirstUse(start, out _);
        var prefetched = HarTimingMap.Map(fresh, prefetchedConnection);
        Assert.AreEqual(-1, prefetched.DnsMs);
        Assert.AreEqual(-1, prefetched.ConnectMs);
        Assert.AreEqual(-1, prefetched.SslMs);
        Assert.IsFalse(prefetched.Reused);
    }

    [TestMethod]
    public void Map_RetryGap_StaysInBlocked()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timing = new HttpRequestTiming(start)
        {
            RequestHeadersReceivedAt = start.AddMilliseconds(10),
            ConnectionReadyAt = start.AddMilliseconds(200),
            RequestSentAt = start.AddMilliseconds(210),
            ResponseHeadersReceivedAt = start.AddMilliseconds(260),
            CompletedAt = start.AddMilliseconds(280),
            AttemptCount = 2,
        };
        var connection = new UpstreamConnectionTiming(start.AddMilliseconds(170));
        connection.DnsResolvedAt = start.AddMilliseconds(180);
        connection.TcpConnectedAt = start.AddMilliseconds(200);
        connection.EstablishedAt = start.AddMilliseconds(200);

        var phases = HarTimingMap.Map(timing, connection);
        Assert.IsTrue(phases.BlockedMs >= 150, "earlier attempt should remain in blocked");
        Assert.AreEqual(SessionDisplayFormat.RoundMs(timing.TotalDuration.TotalMilliseconds), HarTimingMap.Sum(phases), 0.05);
    }

    [TestMethod]
    public void HarRoundTrip_KeepsPhases_AndOldFileIsEstimatedWithoutBars()
    {
        var snap = new SessionSnapshot
        {
            Id = 7,
            Method = "GET",
            Url = "https://example.com/a",
            Host = "example.com",
            StartedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            StatusCode = 200,
            DurationMs = 180,
            TtfbMs = 100,
            BlockedMs = 10,
            DnsMs = 10,
            ConnectMs = 30,
            TlsMs = 15,
            SendMs = 5,
            WaitMs = 100,
            ReceiveMs = 25,
            ConnectionReused = false,
            TimingRecorded = true,
            TimingAttempts = 1,
        };

        var entry = SessionArchive.ToHarEntry(snap);
        using var doc = JsonDocument.Parse(entry.ToJsonString());
        var imported = SessionArchive.FromHarEntry(doc.RootElement, 1);
        Assert.IsNotNull(imported);
        Assert.AreEqual(180, imported!.DurationMs);
        Assert.AreEqual(100, imported.TtfbMs);
        Assert.IsFalse(imported.TimingEstimated);
        Assert.AreEqual(10, imported.BlockedMs);
        Assert.AreEqual(30, imported.ConnectMs);
        Assert.AreEqual(15, imported.TlsMs);
        Assert.AreEqual(25, imported.ReceiveMs);

        const string oldHar = """
            {"log":{"entries":[{"startedDateTime":"2026-01-01T00:00:00Z","time":80,"request":{"method":"GET","url":"https://example.com/old","headers":[]},"response":{"status":200,"headers":[]},"timings":{"blocked":-1,"dns":-1,"connect":-1,"ssl":-1,"send":4,"wait":48,"receive":28}}]}}
            """;
        using var oldDoc = JsonDocument.Parse(oldHar);
        var old = SessionArchive.ParseHarDocument(oldDoc.RootElement, 1);
        Assert.AreEqual(1, old.Count);
        Assert.AreEqual(80, old[0].DurationMs);
        Assert.AreEqual(48, old[0].TtfbMs);
        Assert.IsTrue(old[0].TimingEstimated);
        Assert.IsNull(old[0].BlockedMs);
        Assert.IsNull(old[0].ConnectMs);
        Assert.IsNull(old[0].ReceiveMs);
    }

    [TestMethod]
    public async Task SpillReload_KeepsRecordedPhases()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "ti-timing-" + Guid.NewGuid().ToString("N"));
        using var registry = new SessionRegistry(
            new SessionStoreOptions
            {
                MaxSessionsInMemory = 100,
                SpillBodiesToDisk = true,
                DiskCacheMaxBytes = 64L * 1024 * 1024,
            },
            cacheDir);
        registry.Add(new SessionSnapshot
        {
            Id = 4,
            Method = "GET",
            Url = "https://example.com/spill",
            StatusCode = 200,
            DurationMs = 40,
            TtfbMs = 20,
            RequestBodyText = "spill-body",
            RequestBodyBytes = new byte[32],
            ResponseBodyText = "ok",
            ResponseBodyBytes = new byte[32],
            RequestBodyCapture = BodyCaptureState.Complete,
            ResponseBodyCapture = BodyCaptureState.Complete,
            BlockedMs = 5,
            SendMs = 5,
            WaitMs = 20,
            ReceiveMs = 10,
            TimingRecorded = true,
        });
        await registry.Store.FlushSpillAsync();

        var har = Directory.GetFiles(cacheDir, "*.har", SearchOption.AllDirectories).Single();
        var loaded = await SessionArchive.ImportHarAsync(har);
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(40, loaded[0].DurationMs);
        Assert.AreEqual(20, loaded[0].TtfbMs);
        Assert.IsFalse(loaded[0].TimingEstimated);
        Assert.AreEqual(5, loaded[0].BlockedMs);
        Assert.AreEqual(10, loaded[0].ReceiveMs);
        Assert.AreEqual(20, loaded[0].WaitMs);
        registry.Dispose();
        Directory.Delete(cacheDir, recursive: true);
    }

    private static HttpRequestTiming FirstUse(DateTime start, out UpstreamConnectionTiming connection)
    {
        var timing = new HttpRequestTiming(start)
        {
            RequestHeadersReceivedAt = start.AddMilliseconds(10),
            ConnectionReadyAt = start.AddMilliseconds(50),
            RequestSentAt = start.AddMilliseconds(55),
            ResponseHeadersReceivedAt = start.AddMilliseconds(155),
            CompletedAt = start.AddMilliseconds(180),
            AttemptCount = 1,
        };
        connection = new UpstreamConnectionTiming(start.AddMilliseconds(10));
        connection.DnsResolvedAt = start.AddMilliseconds(20);
        connection.TcpConnectedAt = start.AddMilliseconds(35);
        connection.TlsHandshakeCompletedAt = start.AddMilliseconds(50);
        connection.EstablishedAt = start.AddMilliseconds(50);
        return timing;
    }
}
