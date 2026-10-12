using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionEventTests
{
    [TestMethod]
    public void Buffer_KeepsTheFirstTwenty()
    {
        var buffer = new SessionEventBuffer();
        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < 25; i++)
        {
            buffer.Add("stats.event.rule", failure: false);
        }

        var notes = buffer.Freeze(started);
        Assert.AreEqual(SessionEventBuffer.Cap, notes.Count);
        Assert.AreEqual("stats.event.rule", notes[0].Key);
    }

    [TestMethod]
    public void HarRoundTrip_KeepsEvents_AndSpillCopiesThem()
    {
        var snap = new SessionSnapshot
        {
            Id = 3,
            Method = "GET",
            Url = "https://example.com/e",
            StatusCode = 200,
            DurationMs = 12,
            Events =
            [
                new SessionEventNote(4, "stats.event.rule", false),
                new SessionEventNote(9, "stats.event.tls", true),
            ],
        };

        var entry = SessionArchive.ToHarEntry(snap);
        using var doc = JsonDocument.Parse(entry.ToJsonString());
        var imported = SessionArchive.FromHarEntry(doc.RootElement, 1);
        Assert.IsNotNull(imported);
        Assert.AreEqual(2, imported!.Events!.Count);
        Assert.AreEqual("stats.event.tls", imported.Events[1].Key);
        Assert.IsTrue(imported.Events[1].Failure);

        var cacheDir = Path.Combine(Path.GetTempPath(), "ti-events-" + Guid.NewGuid().ToString("N"));
        using var registry = new SessionRegistry(
            new SessionStoreOptions
            {
                MaxSessionsInMemory = 20,
                SpillBodiesToDisk = true,
                DiskCacheMaxBytes = 64L * 1024 * 1024,
            },
            cacheDir);
        snap.RequestBodyText = "body";
        snap.RequestBodyBytes = new byte[8];
        snap.ResponseBodyText = "ok";
        snap.ResponseBodyBytes = new byte[8];
        registry.Add(snap);
        registry.Store.FlushSpillAsync().GetAwaiter().GetResult();
        var har = Directory.GetFiles(cacheDir, "*.har", SearchOption.AllDirectories).Single();
        var loaded = SessionArchive.ImportHarAsync(har).GetAwaiter().GetResult();
        var spilled = loaded[0].Events;
        Assert.IsNotNull(spilled);
        Assert.AreEqual(2, spilled.Count);
        Assert.AreEqual(4, spilled[0].OffsetMs);
        Directory.Delete(cacheDir, recursive: true);
    }

    [TestMethod]
    public void Statistics_ShowsEventsOnlyWhenPresent()
    {
        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-events-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            vm.SelectedSession = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                StatusCode = 200,
                DurationMs = 10,
                TimingRecorded = true,
                WaitMs = 10,
            };
            vm.SelectedInspectTabIndex = (int)InspectTab.Statistics;
            Assert.IsFalse(vm.ShowStatisticsEvents);

            vm.SelectedSession = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://example.com/paused",
                StatusCode = 200,
                Events = [new SessionEventNote(12, "stats.event.breakpoint", false)],
            };
            Assert.IsTrue(vm.ShowStatisticsEvents);
            Assert.AreEqual("+12 ms", vm.StatisticsEvents[0].TimeText);
            StringAssert.Contains(vm.StatisticsEvents[0].Text, "breakpoint");
            StringAssert.Contains(vm.StatisticsCopyText, "+12 ms");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task PlainRequest_RecordsNothing_CachingRecordsOneEvent()
    {
        using var origin = new HttpListener();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        origin.Prefixes.Add($"http://127.0.0.1:{port}/");
        origin.Start();
        _ = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                var ctx = await origin.GetContextAsync();
                var bytes = "ok"u8.ToArray();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = true,
        };
        var finished = new List<SessionSnapshot>();
        var finishedGate = new object();
        interception.SessionUpdated += (_, snap) =>
        {
            if (snap.DurationMs is null)
            {
                return;
            }

            lock (finishedGate)
            {
                finished.Add(snap);
            }
        };

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            })
            { Timeout = TimeSpan.FromSeconds(8) };

            await http.GetAsync($"http://127.0.0.1:{port}/plain");
            var plain = await WaitFinished(finished, finishedGate, "/plain");
            Assert.IsNull(plain.Events);

            interception.DisableCaching = true;
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/cached");
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"abc\"");
            await http.SendAsync(request);
            var cached = await WaitFinished(finished, finishedGate, "/cached");
            Assert.IsNotNull(cached.Events);
            Assert.AreEqual(1, cached.Events!.Count);
            Assert.AreEqual("stats.event.cache", cached.Events[0].Key);
        }
        finally
        {
            interception.EnsureShutdown();
            origin.Stop();
        }
    }

    private static async Task<SessionSnapshot> WaitFinished(List<SessionSnapshot> finished, object gate, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            SessionSnapshot? found;
            lock (gate)
            {
                found = finished.LastOrDefault(snap => snap.Url?.Contains(path, StringComparison.Ordinal) == true);
            }

            if (found is not null)
            {
                return found;
            }

            await Task.Delay(30);
        }

        Assert.Fail("Timed out waiting for " + path);
        return null!;
    }
}
