using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class LiveSessionUpdateUxTests
{
    [TestMethod]
    public void SessionUpdated_ErrorsOnlyFilter_AddsRowWhenStatusBecomesError()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-live-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            vm.ErrorsOnlyFilter = true;
            var snap = new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                Url = "https://api.example/x",
                Host = "api.example",
                StatusCode = 200,
            };
            vm.SeedSession(snap);
            Assert.AreEqual(0, vm.Sessions.Count, "200 should be hidden by is:error");

            snap.StatusCode = 500;
            // Raise the same event path as the live proxy pipeline.
            typeof(InterceptionService)
                .GetField("SessionUpdated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            // SessionUpdated is an event — invoke via InterceptionService public raise isn't available.
            // Drive through the wired handler by publishing NotifyUpdated + re-running filter logic
            // the same way SessionUpdated does in the VM (call store + filter via Seed path).
            // Use reflection to invoke private OnSessionUpdatedForFilter is fragile; raise event:
            RaiseSessionUpdated(interception, snap);

            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreSame(snap, vm.Sessions[0]);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SessionUpdated_Selected_ShowsWsTabWhenUpgraded()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-live-ws-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var snap = new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://ws.example/sock",
                Host = "ws.example",
                StatusCode = 101,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            Assert.IsFalse(vm.ShowWsFramesTab);

            snap.IsWebSocket = true;
            snap.WebSocketFrames =
            [
                new WebSocketFrameSnapshot { Direction = "Client", Opcode = "Text", PayloadPreview = "hi" },
            ];
            RaiseSessionUpdated(interception, snap);

            Assert.IsTrue(vm.ShowWsFramesTab);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void OpaqueReason_RaisesInpc()
    {
        var snap = new SessionSnapshot();
        var names = new List<string?>();
        snap.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        snap.OpaqueReason = OpaqueTunnelReason.LearnedFailure;
        CollectionAssert.Contains(names, nameof(SessionSnapshot.OpaqueReason));
        CollectionAssert.Contains(names, nameof(SessionSnapshot.OpaqueReasonDisplay));
        Assert.AreEqual(OpaqueTunnelReason.LearnedFailure, snap.OpaqueReason);
        StringAssert.Contains(snap.OpaqueReasonDisplay, "auto-tunneled", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SessionStore_ApplyOptions_EvictsImmediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-apply-opt-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 10,
                    HotBodySessions = 10,
                    SpillBodiesToDisk = false,
                    MaxCaptureBytesInMemory = long.MaxValue,
                },
                dir);
            for (var i = 1; i <= 5; i++)
            {
                store.Add(new SessionSnapshot { Id = i, Method = "GET", Url = $"https://e/{i}" });
            }

            Assert.AreEqual(5, store.Count);
            store.ApplyOptions(new SessionStoreOptions
            {
                MaxSessionsInMemory = 2,
                HotBodySessions = 2,
                SpillBodiesToDisk = false,
                MaxCaptureBytesInMemory = long.MaxValue,
            });
            Assert.AreEqual(2, store.Count);
            Assert.IsNull(store.TryGet(1));
            Assert.IsNotNull(store.TryGet(5));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void RaiseSessionUpdated(InterceptionService interception, SessionSnapshot snap)
    {
        var evt = typeof(InterceptionService).GetEvent(nameof(InterceptionService.SessionUpdated));
        Assert.IsNotNull(evt);
        // Field-like event backing field
        var field = typeof(InterceptionService).GetField(
            nameof(InterceptionService.SessionUpdated),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field, "SessionUpdated backing field missing");
        var del = (EventHandler<SessionSnapshot>?)field!.GetValue(interception);
        del?.Invoke(interception, snap);
    }
}
