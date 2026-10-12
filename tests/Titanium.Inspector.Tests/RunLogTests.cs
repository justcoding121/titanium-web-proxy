using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Tests;

[TestClass]
public class RunLogTests
{
    [TestMethod]
    public async Task Ring_KeepsOrder_AndDropsTheOldest()
    {
        using var ring = new RunLogRing(3);
        var logger = ring.CreateLogger("test");
        foreach (var word in new[] { "one", "two", "three", "four", "five" })
        {
            logger.Log(LogLevel.Error, 0, word, null, static (state, _) => state);
        }

        var lines = await WaitFor(ring, 3);
        CollectionAssert.AreEqual(new[] { "Error three", "Error four", "Error five" }, lines.ToArray());
    }

    [TestMethod]
    public async Task Ring_SurvivesAFactoryRebuild()
    {
        using var ring = new RunLogRing(20);
        using var proxy = new ProxyServer(false, false, false);
        proxy.Logging.EnableConsole = false;
        proxy.Logging.EnableFile = false;
        proxy.Logging.MinimumLevel = LogLevel.Error;
        proxy.Logging.AdditionalLoggerProviders.Add(ring);
        proxy.ApplyLoggingConfiguration();
        proxy.Logger.Log(LogLevel.Error, 0, "kept", null, static (state, _) => state);
        proxy.ApplyLoggingConfiguration();
        proxy.Logger.Log(LogLevel.Error, 0, "still", null, static (state, _) => state);

        var lines = await WaitFor(ring, 2);
        CollectionAssert.AreEqual(new[] { "Error kept", "Error still" }, lines.ToArray());
    }

    [TestMethod]
    public void Caps_RoundTripThroughSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-runlog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.LoggingMaxFileSizeMb = 4;
            settings.Current.LoggingMaxRolledFiles = 2;
            settings.Current.LoggingMemoryLines = 400;
            settings.Save();

            var loaded = new SettingsService(path);
            Assert.AreEqual(4, loaded.Current.LoggingMaxFileSizeMb);
            Assert.AreEqual(2, loaded.Current.LoggingMaxRolledFiles);
            Assert.AreEqual(400, loaded.Current.LoggingMemoryLines);
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
    public void EmptyState_Follow_AndCommand()
    {
        LanguageService.Apply("en");
        using var ring = new RunLogRing(20);
        var model = new RunLogViewModel(ring, () => "Error");
        model.Refresh();
        StringAssert.Contains(model.EmptyText, "Log level is Error");
        Assert.IsTrue(model.Following);
        model.NoteScroll(atBottom: false);
        Assert.IsFalse(model.Following);
        model.NoteScroll(atBottom: true);
        Assert.IsTrue(model.Following);

        var path = Path.Combine(Path.GetTempPath(), "twp-runlog-cmd-" + Guid.NewGuid().ToString("N") + ".json");
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
            Assert.IsNotNull(vm.OpenRunLogCommand);
            vm.OpenRunLogCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "main window");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> WaitFor(RunLogRing ring, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var lines = ring.Snapshot();
            if (lines.Count >= count)
            {
                return lines;
            }

            await Task.Delay(15);
        }

        Assert.Fail("Timed out waiting for " + count + " log lines.");
        return [];
    }
}
