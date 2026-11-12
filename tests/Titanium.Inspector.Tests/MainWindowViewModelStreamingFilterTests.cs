using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Session grid filtering while rows are still streaming — uses <see cref="MainWindowViewModel.ApplySessionUpdated"/>
/// (no Avalonia views).
/// </summary>
[TestClass]
public class MainWindowViewModelStreamingFilterTests
{
    [TestMethod]
    public void StatusFilter_RevealsRowWhenStatusArrivesDuringStreaming()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-stream-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SearchQuery = "status:200";

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://stream.example/events",
                Host = "stream.example",
                StatusCode = null,
                ResponseBodyCapture = BodyCaptureState.Streaming,
                ResponseBodyStreamOpen = true,
            };
            vm.SeedSession(snap);
            Assert.AreEqual(0, vm.Sessions.Count, "Null status must not match status:200");

            snap.StatusCode = 200;
            vm.ApplySessionUpdated(snap);

            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreSame(snap, vm.Sessions[0]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void StatusFilter_RemovesRowWhenStreamingSessionBecomesError()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-stream-err-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SearchQuery = "status:200";

            var snap = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://stream.example/fail",
                Host = "stream.example",
                StatusCode = 200,
                ResponseBodyCapture = BodyCaptureState.Streaming,
                ResponseBodyStreamOpen = true,
            };
            vm.SeedSession(snap);
            Assert.AreEqual(1, vm.Sessions.Count);

            snap.StatusCode = 502;
            vm.ApplySessionUpdated(snap);

            Assert.AreEqual(0, vm.Sessions.Count);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ContentTypeFilter_UpdatesWhileBodyStillStreaming()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-stream-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SearchQuery = "content-type:text/event-stream";

            var snap = new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://sse.example/live",
                Host = "sse.example",
                StatusCode = 200,
                ResponseBodyCapture = BodyCaptureState.Streaming,
                ResponseBodyStreamOpen = true,
            };
            vm.SeedSession(snap);
            Assert.AreEqual(0, vm.Sessions.Count);

            snap.ContentType = "text/event-stream";
            vm.ApplySessionUpdated(snap);

            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreSame(snap, vm.Sessions[0]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void HostFilter_KeepsRowVisibleAcrossStreamingChunks()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-stream-host-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SearchQuery = "host:chunk.example";

            var snap = new SessionSnapshot
            {
                Id = 4,
                Method = "GET",
                Url = "https://chunk.example/data",
                Host = "chunk.example",
                StatusCode = 200,
                ResponseBodyCapture = BodyCaptureState.Streaming,
                ResponseBodyStreamOpen = true,
                BodySize = 0,
            };
            vm.SeedSession(snap);
            Assert.AreEqual(1, vm.Sessions.Count);

            snap.BodySize = 4096;
            snap.ResponseBodyText = "data: chunk\n\n";
            vm.ApplySessionUpdated(snap);

            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(4096, vm.Sessions[0].BodySize);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(string settingsPath)
    {
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()));
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
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
