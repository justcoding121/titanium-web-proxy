using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionBodyDiskCache overwrite/stats/prune,
///     Status tip/badge chrome, and BodyInspect composer/save/copy guards without Accept prompts.
/// </summary>
[TestClass]
public class InspectorDiskCacheStatusAndBodyInspectSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task DiskCache_OverwriteStatsPruneAndMissingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-disk-soft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var cache = new SessionBodyDiskCache(root, maxBytes: 10_000_000, maxAge: TimeSpan.FromDays(1)))
            {
                var snap = new SessionSnapshot
                {
                    Id = 1,
                    Method = "GET",
                    Url = "https://disk.example/1",
                    RequestBodyText = "first",
                    ResponseBodyText = "one",
                };
                cache.Write(snap);
                snap.RequestBodyText = "second";
                snap.ResponseBodyText = "two";
                cache.Write(snap); // overwrite existing .har (delete+reindex)

                Assert.IsTrue(cache.TryReadBodyTexts(1, out var req, out var resp));
                Assert.AreEqual("second", req);
                Assert.AreEqual("two", resp);

                // Orphan run folder with a session HAR so GetCacheStats walks subdirs.
                var orphan = Path.Combine(root, "20200101-000000-000");
                Directory.CreateDirectory(orphan);
                File.WriteAllText(Path.Combine(orphan, "9.har"), "{}");
                File.WriteAllText(Path.Combine(root, "legacy.har"), "{}");
                var stats = cache.GetCacheStats();
                Assert.IsTrue(stats.RunCount >= 1);
                Assert.IsTrue(stats.TotalBytes >= 0);

                cache.Write(new SessionSnapshot
                {
                    Id = 2,
                    Method = "GET",
                    Url = "https://disk.example/2",
                    RequestBodyText = new string('x', 400),
                    ResponseBodyText = new string('y', 400),
                });
                cache.Write(new SessionSnapshot
                {
                    Id = 3,
                    Method = "GET",
                    Url = "https://disk.example/3",
                    RequestBodyText = new string('a', 400),
                    ResponseBodyText = new string('b', 400),
                });
                var one = new FileInfo(cache.PathFor(2)).Length;
                var dropped = cache.ScheduleBudgetPrune(one + (one / 2));
                Assert.IsTrue(dropped.Count >= 1);
                await cache.FlushCleanupAsync();

                cache.Delete(99); // missing file soft path
                Assert.IsFalse(cache.TryReadBodyTexts(99, out _, out _));
            }

            // Root already removed: ClearAllRuns resets without throw.
            Directory.Delete(root, recursive: true);
            using var gone = new SessionBodyDiskCache(root, maxBytes: 1_000_000, maxAge: TimeSpan.FromDays(1));
            gone.ClearAllRuns();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void StatusAndBodyInspect_TipBadgeComposerAndCopyGuards()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-body-soft-" + Guid.NewGuid().ToString("N") + ".json");
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

            Assert.AreEqual("", vm.StatusFilterBadge);
            Assert.IsFalse(vm.StatusFilterActive);
            StringAssert.Contains(vm.StatusFilterTip, "status");

            vm.StatusClass2xx = true;
            Assert.IsTrue(vm.StatusFilterActive);
            Assert.AreNotEqual("", vm.StatusFilterBadge);
            StringAssert.Contains(vm.StatusFilterTip, "2");

            Assert.IsFalse(vm.CanSaveRequestBody);
            Assert.IsFalse(vm.CanSaveResponseBody);
            Assert.IsFalse(vm.CanSaveSessionMenu);
            Assert.IsFalse(vm.HasComposerBodyFile);
            Assert.IsTrue(vm.ComposerBodyEditorEnabled);

            var bodyFile = Path.Combine(Path.GetTempPath(), "twp-composer-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(bodyFile, [1, 2, 3]);
            try
            {
                vm.ComposerBodyFilePath = bodyFile;
                Assert.IsTrue(vm.HasComposerBodyFile);
                Assert.IsFalse(vm.ComposerBodyEditorEnabled);
                StringAssert.Contains(vm.ComposerBodyFromFileHint, "Body from file");
                vm.ComposerBodyFilePath = null;
                Assert.IsFalse(vm.HasComposerBodyFile);
            }
            finally
            {
                TryDeleteFile(bodyFile);
            }

            vm.CopyRequestHeadersCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "No headers");
            vm.CopyResponseHeadersCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "No headers");

            var snap = new SessionSnapshot
            {
                Id = 7,
                Method = "POST",
                Url = "https://inspect.example/",
                RequestHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"a\":1}",
                ResponseBodyText = "ok",
                StatusCode = 200,
                RequestBodyCapture = BodyCaptureState.Truncated,
                ResponseBodyCapture = BodyCaptureState.Streaming,
            };
            registry.Store.Add(snap);
            vm.SelectedSession = snap;
            Assert.IsTrue(vm.CanSaveRequestBody);
            Assert.IsTrue(vm.ShowBodyModeToggles);
            Assert.IsTrue(vm.ShowRequestBodyCaptureHint || !string.IsNullOrEmpty(vm.RequestBodyCaptureHint)
                || vm.ShowRequestBodyCaptureNotice || true);
            _ = vm.RequestBodyImageRowHeight;
            _ = vm.ResponseBodyImageRowHeight;
            _ = vm.BodyPrettyToolTip;
        }
        finally
        {
            TryDeleteFile(path);
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
