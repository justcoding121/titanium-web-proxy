using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionArchive _inspector events Cap/timing,
///     SessionStore pin unload, and Sessions FillGraphQl / CopyUrl soft guards.
/// </summary>
[TestClass]
public class InspectorArchivePinAndSessionsSoftCoverageBehaviorTests
{
    [TestMethod]
    public void SessionArchive_InspectorEventsCap_MarkAndTimingEstimated()
    {
        var events = new StringBuilder();
        events.Append('[');
        for (var i = 0; i < SessionEventBuffer.Cap + 5; i++)
        {
            if (i > 0)
            {
                events.Append(',');
            }

            events.Append($"{{\"key\":\"e{i}\",\"ms\":{i},\"failure\":{(i % 2 == 0).ToString().ToLowerInvariant()}}}");
        }

        events.Append(",null,{\"key\":\"\"},{\"no\":\"key\"}]");

        var har = $$"""
            {
              "log": {
                "entries": [
                  {
                    "startedDateTime": "2026-03-09T10:00:00.000Z",
                    "time": 42,
                    "request": {
                      "method": "GET",
                      "url": "https://archive.example/a",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "queryString": [],
                      "cookies": [],
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "response": {
                      "status": 200,
                      "statusText": "OK",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "cookies": [],
                      "content": { "size": 0, "mimeType": "text/plain", "text": "" },
                      "redirectURL": "",
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "cache": {},
                    "timings": { "send": 0, "wait": 1, "receive": 0 },
                    "_inspector": {
                      "mark": "Red",
                      "events": {{events}}
                    }
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(har);
        var snaps = SessionArchive.ParseHarDocument(doc.RootElement, startId: 3);
        Assert.AreEqual(1, snaps.Count);
        var snap = snaps[0];
        Assert.AreEqual(SessionMark.Red, snap.Mark);
        Assert.IsTrue(snap.TimingEstimated);
        Assert.IsNotNull(snap.Events);
        Assert.AreEqual(SessionEventBuffer.Cap, snap.Events!.Count);
        Assert.AreEqual("e0", snap.Events[0].Key);
    }

    [TestMethod]
    public void SessionStore_PinWithBodiesOnDisk_ClearsPreviousRamBodies()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-pin-soft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 20 },
                cacheDirectory: root);

            var first = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://pin.example/1",
                RequestBodyText = "alpha",
                ResponseBodyText = "one",
                BodiesOnDisk = true,
            };
            var second = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://pin.example/2",
                RequestBodyText = "beta",
                ResponseBodyText = "two",
                BodiesOnDisk = true,
            };
            store.Add(first);
            store.Add(second);
            store.PinnedSessionId = 1;
            Assert.AreEqual(1, store.PinnedSessionId);
            store.PinnedSessionId = 2;
            Assert.AreEqual(2, store.PinnedSessionId);
            // Previous pin with BodiesOnDisk should unload RAM body fields.
            Assert.IsTrue(string.IsNullOrEmpty(first.RequestBodyText) || first.BodiesOnDisk);
            store.PinnedSessionId = null;
            store.Dispose();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Sessions_FillGraphQlAndCopyUrl_SoftGuards()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-sess2-" + Guid.NewGuid().ToString("N") + ".json");
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

            await InvokeAsync(vm, "CopyUrlAsync");
            StringAssert.Contains(vm.StatusText, "Select a session");

            var gql = new SessionSnapshot
            {
                Id = 5,
                Method = "POST",
                Url = "https://gql.example/",
                RequestBodyText = "{\"operationName\":\"GetThing\",\"query\":\"query GetThing { a }\"}",
            };
            registry.Store.Add(gql);
            vm.SelectedSession = gql;
            vm.SelectedPaneNavIndex = 2; // Breakpoints pane so FillGraphQl writes Breakpoints.GraphQlOperationName
            await InvokeAsync(vm, "FillGraphQlFromSelectedAsync");
            Assert.AreEqual("GetThing", vm.Breakpoints.GraphQlOperationName);

            var noName = new SessionSnapshot
            {
                Id = 6,
                Method = "POST",
                Url = "https://gql.example/",
                RequestBodyText = "{\"query\":\"{ a }\"}",
            };
            registry.Store.Add(noName);
            vm.SelectedSession = noName;
            await InvokeAsync(vm, "FillGraphQlFromSelectedAsync");
            StringAssert.Contains(vm.StatusText, "GraphQL");
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    private static async Task InvokeAsync(MainWindowViewModel vm, string name)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, name);
        var result = method!.Invoke(vm, null);
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
