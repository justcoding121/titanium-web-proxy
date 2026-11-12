using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: InterceptionService idle shutdown lanes,
///     MainWindowViewModel filter/search guards, SessionRawHttp byte bodies and caps,
///     SessionBodyDiskCache delete-many miss path.
/// </summary>
[TestClass]
public class InspectorVmDiskRawHttpSoftCoverageBehaviorTests
{
    [TestMethod]
    public void InterceptionService_IdleShutdownAndSessionIdReset()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        interception.ApplyDecryptFailureBypassSetting();
        interception.ApplyHttpProtocols();
        interception.ConfigureLogging(new InspectorSettings { LoggingEnableFile = false });
        interception.ResetSessionIdSequence();
        interception.BeginBackgroundShutdown();
        interception.EnsureShutdown();
        interception.Stop();
        interception.Dispose();
        Assert.IsFalse(interception.IsRunning);
    }

    [TestMethod]
    public async Task MainWindowViewModel_SearchAndImportGuards_WithoutSessions()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-raw-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.SearchQuery = "method:GET";
            if (vm.ClearFiltersCommand.CanExecute(null))
            {
                vm.ClearFiltersCommand.Execute(null);
            }

            Assert.IsNotNull(vm.CopyUrlCommand);
            await InvokeAsync(vm, "ImportHarAsync");
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.StatusText));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void SessionRawHttp_ByteBodiesAndMaxBodyClamp()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://bytes.example/",
            Host = "bytes.example",
            RequestHeadersText = "Content-Type: text/plain\r\n",
            RequestBodyBytes = Encoding.UTF8.GetBytes("payload"),
            StatusCode = 200,
            ResponseHeadersText = "Content-Type: text/plain\r\n",
            ResponseBodyBytes = Encoding.UTF8.GetBytes("answer"),
        };
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 0);
        Assert.IsFalse(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "POST /");
        StringAssert.Contains(text, "Host: bytes.example");
        StringAssert.Contains(text, "payload");
        StringAssert.Contains(text, "answer");

        using var capped = new MemoryStream();
        var huge = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://cap.example/",
            ResponseBodyText = new string('x', 64),
            StatusCode = 200,
        };
        var truncated = SessionRawHttp.Write(huge, capped, maxBodyBytes: 8);
        Assert.IsTrue(truncated.Truncated);
    }

    [TestMethod]
    public void SessionBodyDiskCache_DeleteManyMiss_IsHarmless()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-del-many-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 50_000, maxAge: TimeSpan.FromDays(1));
            cache.DeleteMany([404, 405]);
            var stats = cache.GetCacheStats();
            Assert.AreEqual(0, stats.TotalBytes);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static Task InvokeAsync(MainWindowViewModel vm, string name, params object?[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = method!.Invoke(vm, args);
        return result is Task task ? task : Task.CompletedTask;
    }

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
