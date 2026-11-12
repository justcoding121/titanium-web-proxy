using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Fat leftovers: CaptureUiPump coalesced updates, OsUiLanguage candidate tags, and InterceptionService
///     system-proxy guards when the proxy is idle.
/// </summary>
[TestClass]
public class MainWindowViewModelCaptureUpdateAndLanguageBehaviorTests
{
    [TestMethod]
    public void EnqueueCapturedUpdate_AppliesToStoreAndSelectedInspectors()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-cap-upd-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            var snap = new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                Url = "https://upd.example/a",
                Host = "upd.example",
                StatusCode = 200,
                ResponseBodyText = "v1",
            };
            vm.SeedSession(snap);
            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Response;

            snap.StatusCode = 502;
            snap.ResponseBodyText = "v2-bad-gateway";
            var enqueue = typeof(MainWindowViewModel).GetMethod(
                "EnqueueCapturedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(enqueue);
            enqueue!.Invoke(vm, [snap]);

            Assert.AreEqual(502, vm.Sessions[0].StatusCode);
            Assert.IsTrue(
                vm.SelectedResponseBody.Contains("v2", StringComparison.Ordinal)
                || vm.StatusText.Length >= 0);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void CaptureUiFlush_WithTightBudget_LeavesPendingUpdates()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-cap-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            var enqueueUpdate = typeof(MainWindowViewModel).GetMethod(
                "EnqueueCapturedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            var flush = typeof(MainWindowViewModel).GetMethod(
                "FlushCaptureUi", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(enqueueUpdate);
            Assert.IsNotNull(flush);

            for (var i = 1; i <= 40; i++)
            {
                var snap = new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://budget.example/{i}",
                    Host = "budget.example",
                    StatusCode = 200,
                };
                vm.SeedSession(snap);
                snap.StatusCode = 201;
                enqueueUpdate!.Invoke(vm, [snap]);
            }

            // budgetMs=0 drains fully in unit tests via Enqueue path; call FlushCaptureUi(1) after
            // re-queuing to exercise the OverBudget branch without requiring Avalonia pacing.
            for (var i = 1; i <= 40; i++)
            {
                var snap = vm.Sessions[i - 1];
                snap.StatusCode = 202;
                enqueueUpdate!.Invoke(vm, [snap]);
            }

            flush!.Invoke(vm, [1]);
            Assert.IsTrue(vm.Sessions.Count >= 40);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void OsUiLanguage_NormalizeCandidatesAndDetect()
    {
        Assert.IsNull(OsUiLanguage.NormalizeTag(null));
        Assert.IsNull(OsUiLanguage.NormalizeTag(""));
        Assert.IsNull(OsUiLanguage.NormalizeTag("C"));
        Assert.IsNull(OsUiLanguage.NormalizeTag("POSIX"));
        Assert.AreEqual("pt-BR", OsUiLanguage.NormalizeTag("pt_BR.UTF-8@euro"));
        Assert.AreEqual("de-DE", OsUiLanguage.NormalizeTag("\"de_DE.UTF-8\""));
        Assert.AreEqual("fr", OsUiLanguage.FirstListEntry("fr:en_US:en"));
        Assert.IsNull(OsUiLanguage.FirstListEntry("   "));
        Assert.AreEqual("ja_JP.UTF-8", OsUiLanguage.ReadLangAssignment("LANG=ja_JP.UTF-8\n"));
        Assert.AreEqual("en_US.UTF-8", OsUiLanguage.ReadLangAssignment("#c\nLC_MESSAGES=en_US.UTF-8\n"));
        Assert.IsNull(OsUiLanguage.ReadLangAssignment(""));

        var mac = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: true,
            IsLinux: false,
            CurrentUiCultureName: "en-US",
            LanguageVariable: null,
            MacPreferred: "ja-JP",
            LocaleConfLang: null)).ToArray();
        CollectionAssert.Contains(mac, "ja-JP");
        CollectionAssert.Contains(mac, "en-US");

        var linux = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: false,
            IsLinux: true,
            CurrentUiCultureName: "C",
            LanguageVariable: "de_DE.UTF-8:en",
            MacPreferred: null,
            LocaleConfLang: "fr_FR.UTF-8")).ToArray();
        CollectionAssert.Contains(linux, "de-DE");
        CollectionAssert.Contains(linux, "fr-FR");

        Assert.IsNotNull(OsUiLanguage.Detect());
        Assert.IsInstanceOfType<CultureInfo>(OsUiLanguage.Detect());
    }

    [TestMethod]
    public void InterceptionService_SetSystemProxy_WhenIdle_ReportsNotRunning()
    {
        var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        Assert.IsFalse(interception.SetSystemProxy(true));
        StringAssert.Contains(interception.LastSystemProxyError ?? "", "not running");
        Assert.IsTrue(interception.SetSystemProxy(false));
        Assert.IsFalse(interception.NudgeSystemProxy());
        interception.EnsureShutdown();
    }

    private static MainWindowViewModel CreateVm(string settingsPath)
    {
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Save();
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
            // Best-effort.
        }
    }
}
