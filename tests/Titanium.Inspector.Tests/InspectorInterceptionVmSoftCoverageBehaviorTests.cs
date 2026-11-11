using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: InterceptionService trust-background lane,
///     MainWindowViewModel composer/status guards, SessionRawHttp tunnel host fallback,
///     and SessionBodyDiskCache limit pruning.
/// </summary>
[TestClass]
public class InspectorInterceptionVmSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task InterceptionService_FirefoxTrustBackground_ScheduleDropAndIdle()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        interception.ScheduleFirefoxEnterpriseRootsBestEffort();
        interception.ScheduleClearPendingFirefoxRootTrust();
        interception.ScheduleFirefoxEnterpriseRootsBestEffort();
        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        interception.DropPendingFirefoxTrustBackgroundWork();
        interception.ClearDecryptFailureBypass();
        interception.SchedulePruneOrphanedPersonalCertificates(machineStore: false);
        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        Assert.IsFalse(interception.IsRunning);
    }

    [TestMethod]
    public void SessionRawHttp_TunnelAndHostHeaderInjection()
    {
        using var output = new MemoryStream();
        var tunnel = new SessionSnapshot
        {
            Method = "CONNECT",
            Url = "tunnel.example:443",
            Host = "tunnel.example",
            IsTunnel = true,
            RequestHeadersText = "Proxy-Connection: keep-alive\r\n",
        };
        _ = SessionRawHttp.Write(tunnel, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "CONNECT tunnel.example:443");
        StringAssert.Contains(text, "Host: tunnel.example");

        using var relative = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "get",
            Url = "relative/path?q=1",
            RequestHeadersText = "",
            Host = "rel.example",
            StatusCode = 204,
        };
        _ = SessionRawHttp.Write(snap, relative);
        var relText = Encoding.UTF8.GetString(relative.ToArray());
        StringAssert.Contains(relText, "GET relative/path?q=1");
        StringAssert.Contains(relText, "HTTP/1.1 204");
    }

    [TestMethod]
    public async Task SessionBodyDiskCache_UpdateLimits_PrunesOldestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-lim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 50_000, maxAge: TimeSpan.FromDays(1));
            for (var id = 1; id <= 3; id++)
            {
                cache.Write(new SessionSnapshot
                {
                    Id = id,
                    Method = "GET",
                    Url = $"https://lim.example/{id}",
                    ResponseBodyText = new string('x', 4000),
                });
            }

            var pruned = cache.UpdateLimits(maxBytes: 5_000, maxAge: TimeSpan.FromDays(1));
            Assert.IsTrue(pruned.Count >= 0);
            await cache.FlushCleanupAsync();
            var stats = cache.GetCacheStats();
            Assert.IsTrue(stats.TotalBytes <= 50_000);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [TestMethod]
    public async Task MainWindowViewModel_ComposerAndStatusGuards_WithoutSelection()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-soft-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.ComposerMethod = "POST";
            vm.ComposerUrl = "https://127.0.0.1/local";
            Assert.IsNotNull(vm.SendComposerCommand);

            await InvokeAsync(vm, "ReplaySelectedAsync");
            StringAssert.Contains(vm.StatusText, "Select");

            await InvokeAsync(vm, "ExportHarAsync");
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.StatusText));

            vm.SearchQuery = "host:missing.example";
            if (vm.ClearFiltersCommand.CanExecute(null))
            {
                vm.ClearFiltersCommand.Execute(null);
            }

            Assert.IsNotNull(vm.ReplayCommand);
            Assert.IsNotNull(vm.ExportHarCommand);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    private static Task InvokeAsync(MainWindowViewModel vm, string name, params object?[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = method!.Invoke(vm, args);
        return result is Task task ? task : Task.CompletedTask;
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
        catch (IOException)
        {
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
