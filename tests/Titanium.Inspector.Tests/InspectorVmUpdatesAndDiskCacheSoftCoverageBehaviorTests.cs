using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: MainWindowViewModel update status branches and SessionBodyDiskCache
///     generation discard / abandon paths without dialogs or network.
/// </summary>
[TestClass]
public class InspectorVmUpdatesAndDiskCacheSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task CheckUpdates_UpToDate_UsesTransientSuccessStatus()
    {
        const string feedUrl = "http://127.0.0.1/feed.json";
        var path = Path.Combine(Path.GetTempPath(), "twp-upd-ok-" + Guid.NewGuid().ToString("N") + ".json");
        var previousFeed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", feedUrl);
            var settings = new SettingsService(path);
            var updates = new UpdateService(
                settings,
                () => new HttpClient(
                    new JsonHandler(r => r.RequestUri!.ToString() == feedUrl ? """{"version":"0.0.1"}""" : "{}"),
                    disposeHandler: true));
            var vm = CreateVm(settings, updates);
            await vm.CheckUpdatesAsync(promptIfAvailable: false);
            StringAssert.Contains(vm.StatusText, "up to date");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", previousFeed);
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task CheckUpdates_AvailableWithoutPrompt_SetsSuccessOutcomeOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-upd-offer-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var rid = UpdateService.SuggestRid();
            var manifest = "{\"version\":\"99.0.0\",\"products\":{\"inspector\":{\"assets\":{\"" + rid
                + "\":{\"url\":\"http://127.0.0.1/inspector.zip\",\"sha256\":\"abc\"}}}}}";
            var updates = new UpdateService(
                settings,
                () => new HttpClient(new JsonHandler(_ => Json(manifest)), disposeHandler: true));
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "http://127.0.0.1/feed.json");
            var vm = CreateVm(settings, updates);
            await vm.CheckUpdatesAsync(promptIfAvailable: false);
            StringAssert.Contains(vm.StatusText, "99.0.0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", null);
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task CheckUpdates_FeedWarning_UsesWarningOutcome()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-upd-warn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var updates = new UpdateService(
                settings,
                () => new HttpClient(new JsonHandler(_ => Json("not-json")), disposeHandler: true));
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "http://127.0.0.1/feed.json");
            var vm = CreateVm(settings, updates);
            await vm.CheckUpdatesAsync(promptIfAvailable: false);
            StringAssert.Contains(vm.StatusText, "Update check failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", null);
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task DiskCache_AbandonRun_RejectsStaleFolderAndAcceptsNewWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 50_000, maxAge: TimeSpan.FromDays(1));
            var snap = new SessionSnapshot
            {
                Id = 50,
                Method = "GET",
                Url = "https://gen.example/",
                RequestBodyText = "a",
                ResponseBodyText = "b",
            };
            cache.Write(snap);
            _ = cache.AbandonCurrentRun();
            Assert.IsFalse(cache.TryReadBodyTexts(50, out _, out _));

            snap.ResponseBodyText = "after-abandon";
            cache.Write(snap);
            Assert.IsTrue(cache.TryReadBodyTexts(50, out _, out var resp));
            Assert.AreEqual("after-abandon", resp);

            var stats = cache.GetCacheStats();
            Assert.IsTrue(stats.TotalBytes >= 0);
            await cache.FlushCleanupAsync();
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static MainWindowViewModel CreateVm(SettingsService settings, UpdateService updates)
    {
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            updates,
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });
    }

    private static string Json(string body) => body;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDir(string dir)
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

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;

        public JsonHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(request)),
            });
        }
    }
}
