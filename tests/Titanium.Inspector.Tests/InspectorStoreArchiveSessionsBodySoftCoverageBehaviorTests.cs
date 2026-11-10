using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore Clear/Flush, archive body text/bad-base64,
///     FillGraphQl pane guard, SaveBody empty, Progressive empty WritePaced.
/// </summary>
[TestClass]
public class InspectorStoreArchiveSessionsBodySoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task SessionStore_ClearRaisesSessionsCleared_AndFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 20 },
                cacheDirectory: root);
            var cleared = 0;
            store.SessionsCleared += () => cleared++;
            store.Add(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://clear.example/",
                RequestBodyText = "a",
                ResponseBodyText = "b",
            });
            store.Clear();
            Assert.AreEqual(1, cleared);
            Assert.AreEqual(0, store.Sessions.Count);
            await store.FlushDiskCleanupAsync(TimeSpan.FromSeconds(2));
            store.Dispose();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void SessionArchive_BodyTextBadBase64AndFrames()
    {
        var har = """
            {
              "log": {
                "entries": [
                  {
                    "startedDateTime": "2026-03-09T10:00:00.000Z",
                    "time": 1,
                    "request": {
                      "method": "GET",
                      "url": "https://frames.example/",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "queryString": [],
                      "cookies": [],
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "response": {
                      "status": 101,
                      "statusText": "Switching Protocols",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "cookies": [],
                      "content": { "size": 0, "mimeType": "text/plain", "text": "" },
                      "redirectURL": "",
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "cache": {},
                    "timings": { "send": 0, "wait": 0, "receive": 0 },
                    "_inspector": {
                      "requestBodyText": "plain-req",
                      "responseBodyText": "plain-resp",
                      "requestBodyBase64": "!!!not-base64!!!",
                      "upstreamRequestBodyBase64": "",
                      "isWebSocket": true,
                      "opaqueReason": "DecryptOff",
                      "webSocketFrames": [
                        { "Direction": "Client", "Opcode": "Text", "PayloadPreview": "hi" }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(har);
        var snaps = SessionArchive.ParseHarDocument(doc.RootElement, startId: 2);
        Assert.AreEqual(1, snaps.Count);
        var snap = snaps[0];
        Assert.AreEqual("plain-req", snap.RequestBodyText);
        Assert.AreEqual("plain-resp", snap.ResponseBodyText);
        Assert.IsTrue(snap.IsWebSocket);
        Assert.AreEqual(OpaqueTunnelReason.DecryptOff, snap.OpaqueReason);
        Assert.IsNotNull(snap.WebSocketFrames);
        Assert.AreEqual(1, snap.WebSocketFrames!.Count);

        // Corrupt entry is skipped (FromHarEntry catch → null).
        var corrupt = """
            {"log":{"entries":[{"startedDateTime":"not-a-date","request":1,"response":2}]}}
            """;
        using var bad = JsonDocument.Parse(corrupt);
        Assert.AreEqual(0, SessionArchive.ParseHarDocument(bad.RootElement, startId: 1).Count);
    }

    [TestMethod]
    public async Task SessionsAndBodyInspect_PaneGuardAndEmptySave()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-pane-" + Guid.NewGuid().ToString("N") + ".json");
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

            var gql = new SessionSnapshot
            {
                Id = 3,
                Method = "POST",
                Url = "https://gql.example/",
                RequestBodyText = "{\"operationName\":\"Q\",\"query\":\"query Q { a }\"}",
            };
            registry.Store.Add(gql);
            vm.SelectedSession = gql;
            vm.SelectedPaneNavIndex = 0; // Inspect — not Breakpoints/AutoResponder/MapRemote
            await InvokeAsync(vm, "FillGraphQlFromSelectedAsync");
            StringAssert.Contains(vm.StatusText, "Open Breakpoints");

            var empty = new SessionSnapshot
            {
                Id = 4,
                Method = "GET",
                Url = "https://empty.example/",
                RequestBodyCapture = BodyCaptureState.NotCaptured,
            };
            registry.Store.Add(empty);
            vm.SelectedSession = empty;
            await InvokeAsync(vm, "SaveBodyAsync", true);
            StringAssert.Contains(vm.StatusText, "nothing to save");

            vm.ToggleInspectHeadersCommand.Execute(null);
            Assert.IsFalse(vm.CanSaveRequestBody);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [TestMethod]
    public async Task ProgressiveDelivery_EmptyWireAndIdentityBuild()
    {
        await ProgressiveDelivery.WritePacedAsync(
            Stream.Null, [], chargeWireBytes: 0, () => null, CancellationToken.None, sliceBytes: 0);

        var body = Encoding.UTF8.GetBytes(new string('z', ProgressiveDelivery.MinWireBytes + 4));
        var response = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = body,
        };
        response.Headers.AddHeader(new HttpHeader("Content-Length", body.Length.ToString()));
        var plan = ProgressiveDelivery.TryBuild(response, new Request { Method = "GET" }, body, 0, false, false);
        Assert.IsNotNull(plan);
        using var dest = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(
            dest, plan!.Wire, plan.ChargeWireBytes, () => null, CancellationToken.None, sliceBytes: 1024);
        Assert.AreEqual(plan.Wire.Length, dest.Length);
    }

    private static async Task InvokeAsync(MainWindowViewModel vm, string name, params object[] args)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var method = typeof(MainWindowViewModel).GetMethod(name, flags);
        Assert.IsNotNull(method, name);
        var result = method!.Invoke(vm, args.Length == 0 ? null : args);
        if (result is Task task)
        {
            await task;
        }
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

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
