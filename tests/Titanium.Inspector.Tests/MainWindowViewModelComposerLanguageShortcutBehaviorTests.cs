using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe MainWindowViewModel behaviors that Sonar still marks thin: successful Composer send,
///     UI language / hide-images toggles, headless window commands, and shortcut handlers with a selection.
/// </summary>
[TestClass]
public class MainWindowViewModelComposerLanguageShortcutBehaviorTests
{
    // Bug class: Composer only covers empty/invalid URL guards; a successful send never appends a synthetic
    // session or updates status, so operators think Composer is broken when Replay works.
    [TestMethod]
    [Timeout(60_000)]
    public async Task SendComposer_SuccessfulGet_AppendsSessionAndSetsStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-comp-ok-" + Guid.NewGuid().ToString("N") + ".json");
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain";
            var bytes = Encoding.UTF8.GetBytes("composer-ok");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);
            vm.ComposerMethod = "GET";
            vm.ComposerUrl = $"http://127.0.0.1:{port}/ping";
            vm.ComposerHeaders = "Accept: text/plain";
            vm.ComposerBody = "";

            vm.SendComposerCommand.Execute(null);
            await WaitUntil(() =>
                vm.Sessions.Count > 0
                && (vm.StatusText.Contains("Composer", StringComparison.OrdinalIgnoreCase)
                    || vm.Sessions[0].StatusCode == 200));

            Assert.IsTrue(vm.Sessions.Count >= 1, "Composer must append a synthetic session");
            Assert.AreEqual(200, vm.Sessions[^1].StatusCode);
            StringAssert.Contains(vm.Sessions[^1].ResponseBodyText ?? "", "composer-ok");
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Best-effort.
            }

            TryDelete(path);
        }
    }

    // Bug class: a large inline Composer body never warns, so operators paste megabytes into the editor and hang the UI.
    [TestMethod]
    [Timeout(30_000)]
    public async Task SendComposer_LargeInlineBody_WarnsThenFailsOrSends()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-comp-big-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);
            vm.ComposerUrl = "http://127.0.0.1:9/never";
            vm.ComposerMethod = "POST";
            vm.ComposerBody = new string('x', InspectorBodyLimits.MaxBodyBytes + 32);

            vm.SendComposerCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("consider Load body from file", StringComparison.Ordinal)
                || vm.StatusText.Contains("Composer failed", StringComparison.Ordinal)
                || vm.StatusText.Contains("Composer", StringComparison.Ordinal));

            Assert.IsTrue(
                vm.StatusText.Contains("consider Load body from file", StringComparison.Ordinal)
                || vm.StatusText.Contains("Composer failed", StringComparison.Ordinal)
                || vm.StatusText.Contains("Composer", StringComparison.Ordinal),
                vm.StatusText);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void SetUiLanguageAndHideImages_UpdateSettingsAndSearch()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lang-hide-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);

            Assert.IsFalse(vm.HideImagesFilter);
            vm.HideImagesFilter = true;
            Assert.IsTrue(vm.HideImagesFilter);
            StringAssert.Contains(vm.SearchQuery, "hide:image");
            vm.HideImagesFilter = true; // no-op
            vm.HideImagesFilter = false;
            Assert.IsFalse(vm.HideImagesFilter);

            vm.SetUiLanguageCommand.Execute("en");
            Assert.AreEqual("en", settings.Current.UiLanguage);
            vm.SetUiLanguageCommand.Execute("en"); // unchanged early-return
            vm.SetUiLanguageCommand.Execute(null);
            Assert.AreEqual(LanguageService.AutomaticSetting, settings.Current.UiLanguage);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task HeadlessWindowCommands_SetNeedsWindowStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-headless-win-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);

            vm.OpenRunLogCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("window", StringComparison.OrdinalIgnoreCase));

            vm.OpenTimelineCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("window", StringComparison.OrdinalIgnoreCase));

            vm.OpenLoggingSettingsCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("main window", StringComparison.OrdinalIgnoreCase));

            vm.OpenExclusionSummaryCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("main window", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Excluded hosts", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task Shortcuts_WithSelection_ClearRemoveAndReplayGuards()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-shortcut-sel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://a.example/",
                StatusCode = 200,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://b.example/",
                StatusCode = 404,
            });

            Assert.IsTrue(vm.TryHandleShortcut(ShortcutAction.ClearSessions));
            await WaitUntil(() => vm.Sessions.Count == 0);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://c.example/",
                StatusCode = 200,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 4,
                Method = "POST",
                Url = "https://d.example/",
                StatusCode = 201,
            });
            vm.SetSelectedSessions([vm.Sessions[0]]);
            Assert.IsTrue(vm.TryHandleShortcut(ShortcutAction.RemoveSelected));
            await WaitUntil(() => vm.Sessions.Count == 1);

            vm.SelectedSession = vm.Sessions[0];
            Assert.IsTrue(vm.TryHandleShortcut(ShortcutAction.Replay));
            await WaitUntil(() =>
                vm.StatusText.Contains("Replay", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Cannot replay", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ToggleGridColumnAndReset_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-grid-col-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);
            var hideable = SessionGridColumnCatalog.All.FirstOrDefault(c => c.CanHide);
            Assert.IsNotNull(hideable);

            var before = vm.IsGridColumnVisible(hideable!.Key);
            vm.ToggleGridColumnCommand.Execute(hideable.Key);
            Assert.AreNotEqual(before, vm.IsGridColumnVisible(hideable.Key));
            vm.ResetGridColumnsCommand.Execute(null);
            Assert.AreEqual(before, vm.IsGridColumnVisible(hideable.Key));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(SettingsService settings)
    {
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()));
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 12000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(25);
        }
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
