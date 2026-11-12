using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     InterceptionService in-memory trust / nudge edges, BodyInspect empty-selection refresh, and OsUiLanguage
///     tag parsing that remain thin on develop Sonar.
/// </summary>
[TestClass]
public class InterceptionTrustAndBodyInspectBehaviorTests
{
    [TestMethod]
    public void InstallRootCertificate_WithoutProxy_ReturnsFalse()
    {
        var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            FailNextUserTrustInstall = true,
        };

        Assert.IsFalse(interception.InstallRootCertificate(machineStore: false));
        interception.EnsureShutdown();
        interception.EnsureShutdown();
    }

    [TestMethod]
    public void NudgeSystemProxy_WhenNotEnabled_ReturnsFalse()
    {
        var interception = new InterceptionService(new RecordingSystemProxyController());
        Assert.IsFalse(interception.NudgeSystemProxy(stillWanted: () => true));
        Assert.IsFalse(interception.NudgeSystemProxy(stillWanted: () => false));
        interception.EnsureShutdown();
    }

    [TestMethod]
    public void BodyInspect_ClearingSelection_ResetsPreviewFields()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-body-clear-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.SeedSession(new SessionSnapshot
            {
                Id = 9,
                Method = "GET",
                Url = "https://body.example/",
                Host = "body.example",
                StatusCode = 200,
                RequestBodyText = "req",
                ResponseBodyText = "resp",
                ContentType = "text/plain",
            });
            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Response;
            vm.BodyHexMode = true;
            _ = vm.SelectedResponseBody;
            _ = vm.ShowBodyModeToggles;

            vm.SelectedSession = null;
            Assert.AreEqual("", vm.SelectedRequestBody);
            Assert.AreEqual("", vm.SelectedResponseBody);
            Assert.AreEqual(BodyInspectNoticeSeverity.None, vm.RequestBodyCaptureNoticeSeverity);
            Assert.AreEqual(BodyInspectNoticeSeverity.None, vm.ResponseBodyCaptureNoticeSeverity);
            _ = vm.RequestBodyCaptureHint;
            _ = vm.ResponseBodyCaptureHint;
            _ = vm.RequestBodyCaptureNotice;
            _ = vm.ResponseBodyCaptureNotice;
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void OsUiLanguage_NormalizeAndCandidates_CoverMacLinuxAndPosix()
    {
        Assert.IsNull(OsUiLanguage.NormalizeTag("C"));
        Assert.IsNull(OsUiLanguage.NormalizeTag("POSIX"));
        Assert.IsNull(OsUiLanguage.NormalizeTag("  "));
        Assert.AreEqual("pt-BR", OsUiLanguage.NormalizeTag("pt_BR.UTF-8@euro"));
        Assert.AreEqual("de-DE", OsUiLanguage.NormalizeTag("\"de_DE.UTF-8\""));

        Assert.IsNull(OsUiLanguage.FirstListEntry(null));
        Assert.AreEqual("fr_FR", OsUiLanguage.FirstListEntry("fr_FR:en_US"));

        var mac = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: true,
            IsLinux: false,
            CurrentUiCultureName: "en-US",
            LanguageVariable: null,
            MacPreferred: "ja_JP",
            LocaleConfLang: null)).ToArray();
        CollectionAssert.Contains(mac, "ja-JP");
        CollectionAssert.Contains(mac, "en-US");

        var linux = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: false,
            IsLinux: true,
            CurrentUiCultureName: "C",
            LanguageVariable: "sv_SE:en_US",
            MacPreferred: null,
            LocaleConfLang: "nb_NO.UTF-8")).ToArray();
        CollectionAssert.Contains(linux, "sv-SE");
        CollectionAssert.Contains(linux, "nb-NO");

        Assert.IsNull(OsUiLanguage.ReadLangAssignment(null));
        Assert.IsNull(OsUiLanguage.ReadLangAssignment("# only comments\n"));
        Assert.AreEqual("fi_FI.UTF-8", OsUiLanguage.ReadLangAssignment("LC_ALL=xx\nLANG=fi_FI.UTF-8\n"));

        _ = OsUiLanguage.Detect();
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
