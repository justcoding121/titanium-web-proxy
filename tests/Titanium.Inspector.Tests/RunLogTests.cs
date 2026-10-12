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
        CollectionAssert.AreEqual(new[] { "three", "four", "five" }, lines.Select(e => e.Message).ToArray());
        Assert.AreEqual(LogLevel.Error, lines[0].Level);
        Assert.AreEqual("test", lines[0].Category);
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
        CollectionAssert.AreEqual(new[] { "kept", "still" }, lines.Select(e => e.Message).ToArray());
    }

    [TestMethod]
    public async Task Ring_IncludesException_AndTrimsTrailingNewline()
    {
        using var ring = new RunLogRing(10);
        var logger = ring.CreateLogger("cat");
        logger.Log(LogLevel.Error, 0, "boom\n", new InvalidOperationException("x"), static (state, _) => state);
        var lines = await WaitFor(ring, 1);
        Assert.IsFalse(lines[0].Message.EndsWith('\n'));
        StringAssert.Contains(lines[0].Message, "boom");
        StringAssert.Contains(lines[0].Message, "InvalidOperationException");
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
    public void Filter_AndCopy_MatchStructuredEntries()
    {
        LanguageService.Apply("en");
        using var ring = new RunLogRing(20);
        ring.CreateLogger("Cat").Log(LogLevel.Warning, 0, "hello", null, static (s, _) => s);
        ring.CreateLogger("Other").Log(LogLevel.Error, 0, "nope", null, static (s, _) => s);
        SpinWait.SpinUntil(() => ring.Snapshot().Count >= 2, 2000);

        var filtered = RunLogViewModel.FilterEntries(ring.Snapshot(), "Warning", null);
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual(LogLevel.Warning, filtered[0].Level);

        var byText = RunLogViewModel.FilterEntries(ring.Snapshot(), null, "Cat");
        Assert.AreEqual(1, byText.Count);

        var model = new RunLogViewModel(ring, () => "Error");
        model.Level = "Warning";
        model.Refresh();
        StringAssert.Contains(model.CopyText(), "[WARN ]");
        StringAssert.Contains(model.CopyText(), "Cat: hello");
    }

    [TestMethod]
    public void EmptyState_Follow_AndCommand()
    {
        LanguageService.Apply("en");
        using var ring = new RunLogRing(20);
        var model = new RunLogViewModel(ring, () => "Error");
        model.Refresh();
        StringAssert.Contains(model.EmptyText, "Only errors are shown");
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

    [TestMethod]
    public void Clear_EmptiesAndBumpsVersion()
    {
        using var ring = new RunLogRing(10);
        ring.CreateLogger("c").Log(LogLevel.Error, 0, "x", null, static (s, _) => s);
        SpinWait.SpinUntil(() => ring.Snapshot().Count == 1, 2000);
        var before = ring.Version;
        ring.Clear();
        Assert.AreEqual(0, ring.Snapshot().Count);
        Assert.IsTrue(ring.Version > before);
    }

    private static async Task<IReadOnlyList<RunLogEntry>> WaitFor(RunLogRing ring, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var lines = ring.Snapshot();
            if (lines.Count >= count)
            {
                return lines;
            }

            await Task.Delay(20);
        }

        Assert.Fail("ring did not reach " + count);
        return [];
    }
}
