using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>The Options clock is saved, refreshes open rows, and does not rewrite HAR.</summary>
[TestClass]
public class InspectorClockOptionTests
{
    [TestMethod]
    public void Settings_MissingTimeDisplay_LoadsAsThisPc_AndUtcRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-clock-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"bindPort":8866}""");
            var loaded = new SettingsService(path);
            Assert.AreEqual(InspectorClock.Local, loaded.Current.TimeDisplay);

            loaded.Current.TimeDisplay = InspectorClock.Utc;
            loaded.Save();

            var again = new SettingsService(path);
            Assert.AreEqual(InspectorClock.Utc, again.Current.TimeDisplay);
            StringAssert.Contains(File.ReadAllText(path), "\"timeDisplay\": \"Utc\"");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void NotifyTimeDisplayChanged_RaisesStartedText_AndKeepsTheInstant()
    {
        var session = new SessionSnapshot
        {
            StartedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        var before = session.StartedUtc;
        var names = new List<string?>();
        session.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        session.NotifyTimeDisplayChanged();

        CollectionAssert.Contains(names, nameof(SessionSnapshot.StartedDisplay));
        CollectionAssert.Contains(names, nameof(SessionSnapshot.StartedFullDisplay));
        Assert.AreEqual(before, session.StartedUtc);
    }

    [TestMethod]
    public void HarStartedDateTime_StaysUtc_WhenTheDisplayClockIsThisPc()
    {
        var previous = SessionDisplayFormat.Clock;
        try
        {
            SessionDisplayFormat.SetClock(InspectorClock.Local);
            var session = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/a",
                StartedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            };
            session.NotifyTimeDisplayChanged();

            var localHar = StartedDateTime(session);
            SessionDisplayFormat.SetClock(InspectorClock.Utc);
            var utcHar = StartedDateTime(session);

            Assert.AreEqual(localHar, utcHar);
            StringAssert.StartsWith(localHar, "2026-01-02T03:04:05");
            Assert.IsTrue(
                localHar.EndsWith("Z", StringComparison.Ordinal) ||
                localHar.Contains("+00:00", StringComparison.Ordinal),
                localHar);
            Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), session.StartedUtc);
        }
        finally
        {
            SessionDisplayFormat.SetClock(previous);
        }
    }

    [TestMethod]
    public void SetTimeDisplay_RefreshesOpenRows_AndTheStatusLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-clock-vm-" + Guid.NewGuid().ToString("N") + ".json");
        InterceptionService? interception = null;
        var previous = SessionDisplayFormat.Clock;
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var session = new SessionSnapshot
            {
                Id = 4,
                Method = "GET",
                Url = "https://example.com/a",
                StartedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            };
            vm.SeedSession(session);
            var names = new List<string?>();
            session.PropertyChanged += (_, e) => names.Add(e.PropertyName);

            Assert.IsTrue(vm.TimeDisplayIsLocal);
            Assert.IsFalse(vm.SessionCountText.Contains('Z'));

            vm.SetTimeDisplayUtcCommand.Execute(null);

            Assert.IsTrue(vm.TimeDisplayIsUtc);
            Assert.IsFalse(vm.TimeDisplayIsLocal);
            CollectionAssert.Contains(names, nameof(SessionSnapshot.StartedDisplay));
            CollectionAssert.Contains(names, nameof(SessionSnapshot.StartedFullDisplay));
            Assert.AreEqual("2026-01-02 03:04:05.000Z", session.StartedFullDisplay);
            StringAssert.Contains(vm.SessionCountText, "Z");
            Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), session.StartedUtc);
            Assert.AreEqual(InspectorClock.Utc, new SettingsService(path).Current.TimeDisplay);

            vm.SetTimeDisplayLocalCommand.Execute(null);
            Assert.IsTrue(vm.TimeDisplayIsLocal);
            Assert.AreNotEqual("2026-01-02 03:04:05.000Z", session.StartedFullDisplay);
            Assert.IsFalse(vm.SessionCountText.EndsWith('Z'));
            Assert.AreEqual("When the request started (this PC). Hover a row for the full date and time.", SessionDisplayFormat.StartedColumnTip);
        }
        finally
        {
            interception?.Dispose();
            SessionDisplayFormat.SetClock(previous);
            TryDelete(path);
        }
    }

    private static string StartedDateTime(SessionSnapshot session)
    {
        var entry = SessionArchive.ToHarEntry(session);
        return entry["startedDateTime"]!.GetValue<string>();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
