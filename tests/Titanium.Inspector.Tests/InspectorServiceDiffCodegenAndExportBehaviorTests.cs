using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector edges: SessionDiff body/header deltas, codegen curl/table, store export
///     hydration, ProgressiveDelivery empty writes, and UpdateService ResolveAsset misses.
/// </summary>
[TestClass]
public class InspectorServiceDiffCodegenAndExportBehaviorTests
{
    // Bug class: identical sessions still report differences, or binary bodies / header add-remove are skipped.
    [TestMethod]
    public void SessionDiff_IdenticalBinaryAndHeaderDeltas()
    {
        var a = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            Url = "https://a/x",
            StatusCode = 200,
            RequestHeadersText = "A: 1\r\nB: 2\r\n",
            ResponseHeadersText = "X: 1\r\n",
            RequestBodyBytes = [1, 2, 3],
            ResponseBodyText = "ok",
        };
        var identical = SessionDiff.Compare(a, new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            Url = "https://a/x",
            StatusCode = 200,
            RequestHeadersText = "A: 1\r\nB: 2\r\n",
            ResponseHeadersText = "X: 1\r\n",
            RequestBodyBytes = [1, 2, 3],
            ResponseBodyText = "ok",
        });
        Assert.IsFalse(identical.HasDifferences);
        StringAssert.Contains(identical.Text, "identical");

        var changed = SessionDiff.Compare(a, new SessionSnapshot
        {
            Id = 3,
            Method = "POST",
            Url = "https://a/y",
            StatusCode = 500,
            RequestHeadersText = "A: 1\r\nC: 3\r\n",
            ResponseHeadersText = "",
            RequestBodyText = "new",
            ResponseBodyBytes = Encoding.UTF8.GetBytes("err"),
        });
        Assert.IsTrue(changed.HasDifferences);
        StringAssert.Contains(changed.Text, "Method:");
        StringAssert.Contains(changed.Text, "URL:");
        StringAssert.Contains(changed.Text, "Status:");
        StringAssert.Contains(changed.Text, "Request headers");
        StringAssert.Contains(changed.Text, "Response headers");
        StringAssert.Contains(changed.Text, "Request body");
        StringAssert.Contains(changed.Text, "Response body");
        StringAssert.Contains(changed.Text, "differences");
    }

    // Bug class: ToCurl / ToTable drop formula-trigger cells or truncate without setting truncated=true.
    [TestMethod]
    public void SessionRequestCodegen_CurlAndTable_SanitizeAndTruncate()
    {
        var snap = new SessionSnapshot
        {
            Id = 9,
            Method = "POST",
            Url = "https://api.example/v1",
            StatusCode = 201,
            Comment = "=SUM(A1)",
            RequestHeadersText = "Content-Type: application/json\r\nX-Token: secret\r\n",
            RequestBodyText = "{\"a\":1}",
        };
        var curl = SessionRequestCodegen.ToCurl(snap);
        StringAssert.Contains(curl, "curl");
        StringAssert.Contains(curl, "https://api.example/v1");
        StringAssert.Contains(curl, "application/json");

        var rows = new[]
        {
            snap,
            new SessionSnapshot { Id = 10, Method = "GET", Url = "https://h/10", StatusCode = 200, Comment = "+trigger" },
        };
        var table = SessionRequestCodegen.ToTable(rows, out var written, out var truncated);
        Assert.IsFalse(truncated);
        Assert.AreEqual(2, written);
        StringAssert.Contains(table, "Id\tMethod\tStatus\tURL\tComment");
        StringAssert.Contains(table, "'=SUM(A1)");
        StringAssert.Contains(table, "'+trigger");
        Assert.AreEqual("'=SUM(A1)", SessionRequestCodegen.SpreadsheetCell("=SUM(A1)"));
        Assert.AreEqual("safe", SessionRequestCodegen.SpreadsheetCell("safe"));
    }

    // Bug class: WithBodiesForExportAsync leaves spilled bodies unloaded (or never unloads after export), so HAR
    // export writes empty bodies and RAM stays hot.
    [TestMethod]
    public async Task SessionStore_WithBodiesForExport_HydratesThenUnloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 50,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                StatusCode = 200,
                RequestBodyBytes = Encoding.UTF8.GetBytes("req-bytes"),
                ResponseBodyBytes = Encoding.UTF8.GetBytes("resp-bytes"),
            };
            store.Add(snap);
            await store.FlushSpillAsync();
            Assert.IsTrue(snap.BodiesOnDisk);
            Assert.IsNull(snap.RequestBodyBytes);

            byte[]? seenReq = null;
            await store.WithBodiesForExportAsync([snap], async list =>
            {
                Assert.AreEqual(1, list.Count);
                Assert.IsNotNull(list[0].RequestBodyBytes);
                seenReq = list[0].RequestBodyBytes;
                await Task.CompletedTask;
            });
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("req-bytes"), seenReq);
            Assert.IsNull(snap.RequestBodyBytes, "export helper must unload after use when unpinned");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    // Bug class: WritePacedAsync with empty wire / null shaper throws instead of no-op, so progressive delivery
    // crashes the response path for HEAD-sized leftovers.
    [TestMethod]
    public async Task ProgressiveDelivery_WritePaced_EmptyWireAndNullShaper()
    {
        var dest = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(dest, [], chargeWireBytes: 0, () => null, CancellationToken.None);
        Assert.AreEqual(0, dest.Length);

        var wire = Encoding.ASCII.GetBytes("abcdefghij");
        await ProgressiveDelivery.WritePacedAsync(
            dest, wire, chargeWireBytes: 0, () => null, CancellationToken.None, sliceBytes: 3);
        CollectionAssert.AreEqual(wire, dest.ToArray());

        ProgressiveDelivery.AppendVia(new HeaderCollection(), new Version(1, 1), "titanium");
        ProgressiveDelivery.MarkConnectionClose(new HeaderCollection());
    }

    // Bug class: ResolveAsset with empty products returns MSI by mistake on Windows Program Files hosts.
    [TestMethod]
    public void UpdateService_ResolveAsset_EmptyProductsReturnsZipNull()
    {
        var (kind, asset) = UpdateService.ResolveAsset(new InspectorReleaseManifest
        {
            Version = "8.0.0",
            Products = new InspectorProductsBlock
            {
                Inspector = new InspectorProductAssets { Assets = new Dictionary<string, ManifestAsset>() },
            },
        });
        Assert.AreEqual(UpdateApplyKind.Zip, kind);
        Assert.IsNull(asset);

        var (kind2, asset2) = UpdateService.ResolveAsset(new InspectorReleaseManifest { Version = "8.0.0" });
        Assert.AreEqual(UpdateApplyKind.Zip, kind2);
        Assert.IsNull(asset2);

        Assert.IsFalse(string.IsNullOrWhiteSpace(UpdateService.SuggestRid()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(UpdateService.FormatAssemblyDisplayVersion()));
        _ = UpdateService.AssemblyVersion();
        _ = UpdateService.AssemblyInformationalVersion();
    }

    // Bug class: ImportHarAsync on empty/malformed documents throws instead of returning zero sessions.
    [TestMethod]
    public async Task SessionArchive_ImportHar_EmptyAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-har-imp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var empty = Path.Combine(dir, "empty.har");
            await File.WriteAllTextAsync(empty, "{}");
            var none = await SessionArchive.ImportHarAsync(empty);
            Assert.AreEqual(0, none.Count);

            var snap = new SessionSnapshot
            {
                Id = 5,
                Method = "GET",
                Url = "https://round.trip/",
                StatusCode = 204,
                Comment = "note",
            };
            var har = Path.Combine(dir, "one.har");
            await SessionArchive.ExportHarAsync([snap], har);
            var imported = await SessionArchive.ImportHarAsync(har);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual("https://round.trip/", imported[0].Url);
            Assert.AreEqual("note", imported[0].Comment);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
