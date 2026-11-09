using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionSearch is:/hide:/protocol/mark/comment arms,
///     InterceptionService protocol and cache toggles, and MainWindowViewModel export guard.
/// </summary>
[TestClass]
public class InspectorSessionSearchInterceptionSoftCoverageWave18BehaviorTests
{
    private static readonly List<SessionSnapshot> Sample =
    [
        new()
        {
            Id = 1,
            Method = "POST",
            Url = "https://api.example/rpc",
            Host = "api.example",
            StatusCode = 200,
            Protocol = "GRPC",
            IsGrpc = true,
            ContentType = "application/grpc",
            Comment = "saved for review",
            Mark = SessionMark.Red,
        },
        new()
        {
            Id = 2,
            Method = "GET",
            Url = "https://cdn.example/app.png",
            Host = "cdn.example",
            StatusCode = 200,
            IsMultipart = true,
            ContentType = "multipart/form-data",
        },
        new()
        {
            Id = 3,
            Method = "CONNECT",
            Url = "https://opaque.example:443",
            Host = "opaque.example",
            IsTunnel = true,
            OpaqueReason = OpaqueTunnelReason.DecryptOff,
        },
    ];

    [TestMethod]
    public void SessionSearch_IsHideProtocolMarkComment_AndCustomBodyMatcher()
    {
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "is:grpc").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "is:multipart").Count());
        Assert.AreEqual(0, SessionSearch.Filter(Sample, "hide:image").Count(s => s.Id == 2));
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "protocol:grpc").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "content-type:multipart").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "mark:red").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "comment:review").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "is:opaque-reason:decryptoff").Count());

        Assert.AreEqual(3, SessionSearch.Filter(Sample, "-bogusfield:x").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "body:needle", (s, _) => s.Id == 1).Count());
        Assert.AreEqual(
            "host:a is:grpc",
            SessionSearch.ToggleToken("host:a", "is", "grpc"));
        Assert.AreEqual("host:a", SessionSearch.ToggleToken("host:a is:grpc", "is", "grpc"));
    }

    [TestMethod]
    public void InterceptionService_ProtocolAndCacheToggles_AffectPreferences()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            PreferHttp2 = false,
            PreferHttp3 = false,
            DisableCaching = true,
            ForceUncompressed = true,
        };
        service.ApplyHttpProtocols();
        Assert.IsFalse(service.Http2Enabled);
        Assert.IsFalse(service.Http3Enabled);
        Assert.IsTrue(service.DisableCaching);
        Assert.IsTrue(service.ForceUncompressed);

        service.PreferHttp2 = true;
        service.PreferHttp3 = true;
        service.ApplyHttpProtocols();
        Assert.IsTrue(service.Http2Enabled);
        Assert.IsTrue(service.Http3Enabled);
    }

    [TestMethod]
    public async Task MainWindowViewModel_ExportHar_WithoutSelection_SetsStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-export-guard-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });
            await InvokeAsync(vm, "ExportHarAsync");
            StringAssert.Contains(vm.StatusText, "No sessions to export", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(path);
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
}
