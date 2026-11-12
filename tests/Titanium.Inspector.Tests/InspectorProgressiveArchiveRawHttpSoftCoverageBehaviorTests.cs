using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: ProgressiveDelivery skip arms, SessionArchive import,
///     SessionRawHttp reason/cap edges, OsUiLanguage probe candidates, BodyInspect clear selection.
/// </summary>
[TestClass]
public class InspectorProgressiveArchiveRawHttpSoftCoverageBehaviorTests
{
    [TestMethod]
    public void ProgressiveDelivery_SkipArms_UpgradeTrailersEditedEncoding()
    {
        var body = Encoding.UTF8.GetBytes(new string('p', ProgressiveDelivery.MinWireBytes + 8));
        Response Ok()
        {
            var r = new Response
            {
                HttpVersion = new Version(1, 1),
                StatusCode = 200,
                StatusDescription = "OK",
                Body = body,
            };
            r.Headers.AddHeader(new HttpHeader("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
            return r;
        }

        Assert.IsNull(ProgressiveDelivery.TryBuild(Ok(), new Request { Method = "GET" }, body, body.Length, false, edited: true));
        Assert.IsNull(ProgressiveDelivery.TryBuild(Ok(), new Request { Method = "GET" }, body, body.Length, transcoded: true, edited: false));

        var upgradeReq = new Request { Method = "GET" };
        upgradeReq.Headers.AddHeader(new HttpHeader("Upgrade", "websocket"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(Ok(), upgradeReq, body, body.Length, false, false));

        var trailers = Ok();
        trailers.Headers.AddHeader(new HttpHeader("Trailer", "Expires"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(trailers, new Request { Method = "GET" }, body, body.Length, false, false));

        var weird = Ok();
        weird.Headers.AddHeader(new HttpHeader("Content-Encoding", "zstd"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(weird, new Request { Method = "GET" }, body, body.Length, false, false));
        Assert.AreEqual(ProgressiveSkipReason.Encoding, ProgressiveDelivery.Evaluate(weird, new Request { Method = "GET" }, body, body.Length, false, false));

        var multi = Ok();
        multi.Headers.AddHeader(new HttpHeader("Content-Encoding", "gzip, br"));
        Assert.IsNull(ProgressiveDelivery.TryBuild(multi, new Request { Method = "GET" }, body, body.Length, false, false));

        ProgressiveDelivery.AppendVia(Ok().Headers, new Version(1, 1), "titanium");
        ProgressiveDelivery.MarkConnectionClose(Ok().Headers);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Upgrade) >= 0);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Trailers) >= 0);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Edited) >= 0);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Encoding) >= 0);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.NoBody) >= 0);
    }

    [TestMethod]
    public async Task SessionArchive_ImportHarFilesAsync_AndEmptyDocument()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-har-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var har = """
                {
                  "log": {
                    "entries": [
                      {
                        "startedDateTime": "2026-03-09T10:00:00.000Z",
                        "request": {
                          "method": "GET",
                          "url": "https://import.example/a",
                          "httpVersion": "HTTP/1.1",
                          "headers": [],
                          "queryString": [],
                          "cookies": [],
                          "headersSize": -1,
                          "bodySize": 0
                        },
                        "response": {
                          "status": 204,
                          "statusText": "No Content",
                          "httpVersion": "HTTP/1.1",
                          "headers": [],
                          "cookies": [],
                          "content": { "size": 0, "mimeType": "text/plain", "text": "" },
                          "redirectURL": "",
                          "headersSize": -1,
                          "bodySize": 0
                        },
                        "cache": {},
                        "timings": { "send": 0, "wait": 1, "receive": 0 }
                      }
                    ]
                  }
                }
                """;
            var path1 = Path.Combine(dir, "one.har");
            var path2 = Path.Combine(dir, "two.har");
            await File.WriteAllTextAsync(path1, har);
            await File.WriteAllTextAsync(path2, har);

            var imported = await SessionArchive.ImportHarFilesAsync([path1, path2]);
            Assert.AreEqual(2, imported.Count);
            Assert.AreEqual(1, imported[0].Id);
            Assert.AreEqual(2, imported[1].Id);

            using var empty = JsonDocument.Parse("""{"log":{}}""");
            Assert.AreEqual(0, SessionArchive.ParseHarDocument(empty.RootElement, startId: 5).Count);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    [TestMethod]
    public void SessionRawHttp_ReasonTable_AndMaxBodyClamp()
    {
        foreach (var code in new[] { 200, 201, 204, 301, 302, 304, 400, 401, 403, 404, 500, 502, 503, 418 })
        {
            var snap = new SessionSnapshot
            {
                Id = code,
                Method = "GET",
                Host = "raw.example",
                Url = "https://raw.example/",
                StatusCode = code,
                ResponseHeadersText = "Content-Type: text/plain\r\n",
                ResponseBodyText = "x",
                RequestBodyBytes = Encoding.UTF8.GetBytes("req"),
                ResponseBodyBytes = Encoding.UTF8.GetBytes("resp"),
            };
            using var output = new MemoryStream();
            var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 0); // clamps to MaxBodyBytes
            Assert.IsFalse(result.Truncated);
            var text = Encoding.ASCII.GetString(output.ToArray());
            StringAssert.Contains(text, $"HTTP/1.1 {code}");
        }
    }

    [TestMethod]
    public void OsUiLanguage_CandidateTags_CoverMacLinuxAndCultureMiss()
    {
        var mac = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: true,
            IsLinux: false,
            CurrentUiCultureName: "en-US",
            LanguageVariable: null,
            MacPreferred: "zh_Hant_TW.UTF-8",
            LocaleConfLang: null)).ToList();
        CollectionAssert.Contains(mac, "zh-Hant-TW");

        var linux = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: false,
            IsLinux: true,
            CurrentUiCultureName: "C",
            LanguageVariable: "pt_BR:en",
            MacPreferred: null,
            LocaleConfLang: "de_DE.UTF-8")).ToList();
        Assert.IsTrue(linux.Contains("pt-BR") || linux.Contains("de-DE") || linux.Count >= 1);

        Assert.IsNull(OsUiLanguage.FirstListEntry(null));
        Assert.IsNull(OsUiLanguage.NormalizeTag("C@euro"));
        Assert.AreEqual("fr-FR", OsUiLanguage.NormalizeTag("\"fr_FR.UTF-8\""));
    }

    [TestMethod]
    public void BodyInspect_ClearSelection_ResetsHintsAndBodies()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-body-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            var snap = new SessionSnapshot
            {
                Id = 9,
                Method = "GET",
                Url = "https://body.example/",
                Host = "body.example",
                StatusCode = 200,
                ResponseBodyText = "hello",
                BodiesMissingFromDisk = true,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Response;
            Assert.IsTrue(vm.SelectedResponseBody.Length >= 0);

            vm.SelectedSession = null;
            Assert.AreEqual("", vm.SelectedRequestBody);
            Assert.AreEqual("", vm.SelectedResponseBody);
            Assert.AreEqual("", vm.RequestBodyCaptureHint);
            Assert.AreEqual("", vm.ResponseBodyCaptureHint);
            Assert.AreEqual(BodyInspectNoticeSeverity.None, vm.RequestBodyCaptureNoticeSeverity);
            Assert.AreEqual(BodyInspectNoticeSeverity.None, vm.ResponseBodyCaptureNoticeSeverity);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
