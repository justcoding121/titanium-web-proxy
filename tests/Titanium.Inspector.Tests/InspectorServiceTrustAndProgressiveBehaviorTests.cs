using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector edges: progressive skip counters, LinkShaper overflow/dispose, trust copy,
///     SessionStoreOptions defaults, and SessionSearch mark/protocol filters.
/// </summary>
[TestClass]
public class InspectorServiceTrustAndProgressiveBehaviorTests
{
    // Bug class: Upgrade / Trailers / Encoding / Transcoded / Edited / NoBody skips never bump FallbackCount, so
    // progressive metrics under-count why paced delivery fell back.
    [TestMethod]
    public void ProgressiveDelivery_RemainingSkipReasons_BumpFallbackCounters()
    {
        var body = new byte[ProgressiveDelivery.MinWireBytes];
        var beforeUpgrade = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Upgrade);
        var beforeTrailers = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Trailers);
        var beforeEncoding = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Encoding);
        var beforeTranscoded = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Transcoded);
        var beforeEdited = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Edited);
        var beforeNoBody = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.NoBody);

        var upgrade = ResponseOf(200, body);
        upgrade.Headers.AddHeader(new HttpHeader("Upgrade", "websocket"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(upgrade, RequestOf("GET"), body, body.Length, false, false));

        var trailers = ResponseOf(200, body);
        trailers.Headers.AddHeader(new HttpHeader("Trailer", "Checksum"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(trailers, RequestOf("GET"), body, body.Length, false, false));

        var encoding = ResponseOf(200, body);
        encoding.Headers.AddHeader(new HttpHeader("Content-Encoding", "zstd"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(encoding, RequestOf("GET"), body, body.Length, false, false));

        Assert.IsNull(ProgressiveDelivery.TryBuild(ResponseOf(200, body), RequestOf("GET"), body, body.Length, true, false));
        Assert.IsNull(ProgressiveDelivery.TryBuild(ResponseOf(200, body), RequestOf("GET"), body, body.Length, false, true));
        Assert.IsNull(ProgressiveDelivery.TryBuild(ResponseOf(200, []), RequestOf("GET"), [], 0, false, false));

        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Upgrade) > beforeUpgrade);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Trailers) > beforeTrailers);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Encoding) > beforeEncoding);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Transcoded) > beforeTranscoded);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Edited) > beforeEdited);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.NoBody) > beforeNoBody);

        var plain = Encoding.UTF8.GetBytes(new string('p', ProgressiveDelivery.MinWireBytes));
        var plan = ProgressiveDelivery.TryBuild(ResponseOf(200, plain), RequestOf("GET"), plain, plain.Length, false, false);
        Assert.IsNotNull(plan);

        var headers = new HeaderCollection();
        ProgressiveDelivery.AppendVia(headers, HttpVersion.Version11, "titanium");
        ProgressiveDelivery.AppendVia(headers, HttpVersion.Version11, "titanium");
        ProgressiveDelivery.AppendVia(headers, HttpVersion.Version20, "titanium");
        ProgressiveDelivery.MarkConnectionClose(headers);
        Assert.AreEqual("close", headers.GetHeaders("Connection")![0].Value);
        Assert.IsTrue(headers.HeaderExists("Via"));
    }

    // Bug class: Reserve after Dispose still advances clocks, or overflowing byteCount throws instead of capping.
    [TestMethod]
    public void LinkShaper_DisposedAndOverflow_DoNotThrow()
    {
        var time = new ManualTime();
        using (var shaper = new LinkShaper(new NetworkThrottleProfile("t", 0, 1_000), time, static (_, _) => Task.CompletedTask))
        {
            Assert.AreEqual(TimeSpan.Zero, shaper.Reserve(ThrottleDirection.Upload, 0));
            Assert.IsTrue(shaper.Reserve(ThrottleDirection.Upload, long.MaxValue / 2) >= TimeSpan.Zero);
            Assert.IsTrue(shaper.IsEnabled);
            shaper.Dispose();
            Assert.AreEqual(TimeSpan.Zero, shaper.Reserve(ThrottleDirection.Download, 1_000));
            Assert.IsFalse(shaper.IsEnabled);
        }
    }

    // Bug class: FromSettings treats 0 / negative retention knobs as intentional and builds an empty store.
    [TestMethod]
    public void SessionStoreOptions_FromSettings_UsesDefaultsForNonPositive()
    {
        var opts = SessionStoreOptions.FromSettings(new InspectorSettings
        {
            MaxSessionsInMemory = 0,
            DiskCacheMaxBytes = -1,
            MaxCaptureBytesInMemory = 0,
            HotBodySessions = -5,
            SpillBodiesToDisk = false,
            DiskCacheMaxAgeDays = 0,
        });
        Assert.AreEqual(10_000, opts.MaxSessionsInMemory);
        Assert.AreEqual(2L * 1024 * 1024 * 1024, opts.DiskCacheMaxBytes);
        Assert.AreEqual(512L * 1024 * 1024, opts.MaxCaptureBytesInMemory);
        Assert.AreEqual(2_000, opts.HotBodySessions);
        Assert.IsTrue(opts.SpillBodiesToDisk, "FromSettings always spills");
        Assert.AreEqual(7, opts.DiskCacheMaxAgeDays);

        var custom = SessionStoreOptions.FromSettings(new InspectorSettings
        {
            MaxSessionsInMemory = 42,
            DiskCacheMaxBytes = 99,
            MaxCaptureBytesInMemory = 11,
            HotBodySessions = 3,
            DiskCacheMaxAgeDays = 2,
        });
        Assert.AreEqual(42, custom.MaxSessionsInMemory);
        Assert.AreEqual(99, custom.DiskCacheMaxBytes);
        Assert.AreEqual(11, custom.MaxCaptureBytesInMemory);
        Assert.AreEqual(3, custom.HotBodySessions);
        Assert.AreEqual(2, custom.DiskCacheMaxAgeDays);
        Assert.IsTrue(custom.SpillBodiesToDisk);
    }

    // Bug class: FormatDecryptTrustFailed collapses every Kind onto the generic title/body, so Mac confirm /
    // certutil / Homebrew recovery dialogs share the wrong primary button.
    [TestMethod]
    public void OsTrustUxCopy_FormatDecryptTrustFailed_CoversEveryKind()
    {
        var kinds = Enum.GetValues<CertificateOsTrustKind>();
        foreach (var kind in kinds)
        {
            var withDetail = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(kind, "detail-" + kind));
            Assert.IsFalse(string.IsNullOrWhiteSpace(withDetail.Title), kind.ToString());
            StringAssert.Contains(withDetail.Body, "detail-" + kind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(withDetail.Primary), kind.ToString());
            Assert.IsTrue(withDetail.Height > 0, kind.ToString());

            var without = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(kind, "  "));
            Assert.IsFalse(string.IsNullOrWhiteSpace(without.Body), kind.ToString());
        }

        var nullResult = OsTrustUxCopy.FormatDecryptTrustFailed(null);
        Assert.IsFalse(string.IsNullOrWhiteSpace(nullResult.Title));
        Assert.IsFalse(string.IsNullOrWhiteSpace(nullResult.Body));
    }

    // Bug class: mark:none / protocol: / contenttype: synonyms never filter, and ImportHarFilesAsync ignores cancel.
    [TestMethod]
    public async Task SessionSearch_MarkProtocolAndImportHarCancel()
    {
        var sessions = new[]
        {
            new SessionSnapshot
            {
                Id = 1,
                Mark = SessionMark.None,
                Protocol = "HTTP/1.1",
                ContentType = "application/json",
                IsGrpc = true,
                Url = "https://a/",
            },
            new SessionSnapshot
            {
                Id = 2,
                Mark = SessionMark.Red,
                Protocol = "HTTP/2",
                ContentType = "text/html",
                IsGrpc = false,
                Url = "https://b/",
            },
        };

        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:none").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:Red").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "protocol:HTTP/2").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "contenttype:json").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "-is:grpc").Count());

        var dir = Path.Combine(Path.GetTempPath(), "twp-har-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var har1 = Path.Combine(dir, "a.har");
            var har2 = Path.Combine(dir, "b.har");
            await File.WriteAllTextAsync(har1, SessionArchive.BuildHarDocumentJson(
                [new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/" }], indented: false));
            await File.WriteAllTextAsync(har2, SessionArchive.BuildHarDocumentJson(
                [new SessionSnapshot { Id = 2, Method = "GET", Url = "https://b/" }], indented: false));

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => SessionArchive.ImportHarFilesAsync([har1, har2], cts.Token));

            var imported = await SessionArchive.ImportHarFilesAsync([har1, har2]);
            Assert.AreEqual(2, imported.Count);
            Assert.AreEqual(1, imported[0].Id);
            Assert.AreEqual(2, imported[1].Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Request RequestOf(string method)
    {
        var request = new Request { Method = method };
        return request;
    }

    private static Response ResponseOf(int status, byte[] body) =>
        new()
        {
            StatusCode = status,
            StatusDescription = "x",
            HttpVersion = new Version(1, 1),
            Body = body,
        };

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => 10_000_000;
    }
}
