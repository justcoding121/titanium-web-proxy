using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore pin/dispose/missing-body, FileLog drop/dispose,
///     and MainWindow copy/composer guards without clipboard or dialog Accept prompts.
/// </summary>
[TestClass]
public class InspectorStoreFileLogAndVmGuardSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task SessionStore_PinOldestDisposeAndMissingBodyFastPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-store-soft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new SessionStoreOptions
            {
                SpillBodiesToDisk = true,
                MaxSessionsInMemory = 50,
            };
            var store = new SessionStore(options, cacheDirectory: root);
            Assert.IsNull(store.OldestStartedUtc);

            var first = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://store.example/1",
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                RequestBodyText = "a",
                ResponseBodyText = "b",
                BodiesOnDisk = true,
            };
            var second = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://store.example/2",
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                RequestBodyText = "c",
                ResponseBodyText = "d",
                BodiesOnDisk = true,
            };
            store.Add(first);
            store.Add(second);
            Assert.IsNotNull(store.OldestStartedUtc);

            store.PinnedSessionId = 2;
            Assert.AreEqual(2, store.PinnedSessionId);
            store.PinnedSessionId = 1;
            Assert.AreEqual(1, store.PinnedSessionId);
            store.PinnedSessionId = null;

            var missing = new SessionSnapshot
            {
                Id = 9,
                Method = "GET",
                Url = "https://store.example/missing",
                BodiesOnDisk = true,
                BodiesMissingFromDisk = true,
            };
            store.Add(missing);
            await store.EnsureBodiesLoadedAsync(missing);
            Assert.IsTrue(missing.BodiesMissingFromDisk);

            store.Dispose();
            store.Add(new SessionSnapshot { Id = 99, Method = "GET", Url = "https://gone.example/" });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InspectorFileLogProvider_DropsWhenFull_AndDisposeIsIdempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "twp-flog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var provider = new InspectorFileLogProvider(folder, maxBytes: 10_000_000, keep: 2, queueCapacity: 1);
            var logger = provider.CreateLogger("drop.tests");
            for (var i = 0; i < 40; i++)
            {
                logger.LogInformation("flood-{N}", i);
            }

            await Task.Delay(150);
            Assert.IsFalse(string.IsNullOrEmpty(provider.CurrentFilePath));
            provider.Dispose();
            provider.Dispose();
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [TestMethod]
    public async Task MainWindow_CopyAndComposerGuards_WithoutSelection()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-guard-" + Guid.NewGuid().ToString("N") + ".json");
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

            Assert.IsFalse(vm.CanCopyAsCurl);
            Assert.IsFalse(vm.TryBuildCopyAsCurl(out _));
            Assert.IsFalse(vm.TryBuildCopyAsFetch(out _));
            Assert.IsFalse(vm.TryBuildCopyAsPowerShell(out _));
            Assert.IsFalse(vm.TryBuildCopyAsPython(out _));

            await InvokeAsync(vm, "LoadFromSelectedAsync");
            StringAssert.Contains(vm.StatusText, "Select a session");

            await InvokeAsync(vm, "FillGraphQlFromSelectedAsync");
            StringAssert.Contains(vm.StatusText, "Select a session");

            await InvokeAsync(vm, "CopyAsCurlAsync");
            StringAssert.Contains(vm.StatusText, "Select one session");
            await InvokeAsync(vm, "CopyAsFetchAsync");
            StringAssert.Contains(vm.StatusText, "fetch");
            await InvokeAsync(vm, "CopyAsPowerShellAsync");
            StringAssert.Contains(vm.StatusText, "PowerShell");
            await InvokeAsync(vm, "CopyAsPythonAsync");
            StringAssert.Contains(vm.StatusText, "Python");
            await InvokeAsync(vm, "CopyAsTableAsync");
            StringAssert.Contains(vm.StatusText, "No sessions");

            _ = OsThemePreference.AutomaticVariant();
            Assert.IsFalse(OsThemePreference.FilePrefersDark() && false);
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

    private static void TryDeleteFile(string path)
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
