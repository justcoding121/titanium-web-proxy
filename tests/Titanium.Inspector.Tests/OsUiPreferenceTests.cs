using Avalonia.Styling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class OsUiPreferenceTests
{
    private static readonly string[] GermanDisplayCulture = ["de-DE"];

    [TestMethod]
    public void Language_WindowsUsesDisplayCulture_NotShellLanguage()
    {
        var tags = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: true,
            IsMac: false,
            IsLinux: false,
            CurrentUiCultureName: "de-DE",
            LanguageVariable: "fr:en",
            MacPreferred: "ja",
            LocaleConfLang: "sv_SE.UTF-8"));

        CollectionAssert.AreEqual(GermanDisplayCulture, tags.ToArray());
    }

    [TestMethod]
    public void Language_MacPrefersAppleLanguagesOverTerminalLocale()
    {
        var tags = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: true,
            IsLinux: false,
            CurrentUiCultureName: "en-US",
            LanguageVariable: "fr",
            MacPreferred: "zh-Hant-TW",
            LocaleConfLang: null));

        Assert.AreEqual("zh-Hant", LanguageService.ResolveCultureName(tags.First()));
    }

    [TestMethod]
    public void Language_LinuxPrefersGettextLanguageThenLocaleConf()
    {
        var fromLanguage = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: false,
            IsLinux: true,
            CurrentUiCultureName: "en-US",
            LanguageVariable: "pt_BR.UTF-8:en",
            MacPreferred: null,
            LocaleConfLang: "de_DE.UTF-8"));
        Assert.AreEqual("pt-BR", LanguageService.ResolveCultureName(fromLanguage.First()));

        var fromFile = OsUiLanguage.CandidateTags(new OsUiLanguage.Probe(
            IsWindows: false,
            IsMac: false,
            IsLinux: true,
            CurrentUiCultureName: "C",
            LanguageVariable: null,
            MacPreferred: null,
            LocaleConfLang: "nn_NO.UTF-8"));
        Assert.AreEqual("nb", LanguageService.ResolveCultureName(fromFile.First()));
    }

    [TestMethod]
    public void Language_LocaleConf_ReadsLangBeforeMessages()
    {
        var text = """
            # comment
            LC_MESSAGES=sv_SE.UTF-8
            LANG="de_DE.UTF-8"
            """;
        Assert.AreEqual("de_DE.UTF-8", OsUiLanguage.ReadLangAssignment(text));
        Assert.AreEqual("sv_SE.UTF-8", OsUiLanguage.ReadLangAssignment("LC_MESSAGES=sv_SE.UTF-8\n"));
    }

    [TestMethod]
    public void Theme_PortalPreference_StaysOnAvaloniaDefault()
    {
        Assert.AreEqual(1, OsThemePreference.ParseColorScheme("(<uint32 1>,)"));
        Assert.AreEqual(2, OsThemePreference.ParseColorScheme("(<uint32 2>,)"));
        Assert.AreEqual(0, OsThemePreference.ParseColorScheme("(<uint32 0>,)"));
        Assert.IsNull(OsThemePreference.ParseColorScheme("Error: portal missing"));
    }

    [TestMethod]
    public void Theme_LinuxFiles_FollowGtkUnlessKdeDesktop()
    {
        const string gtkDark = "gtk-application-prefer-dark-theme=1\n";
        const string gtkLight = "gtk-theme-name=Adwaita\n";
        const string kdeDark = "[General]\nColorScheme=BreezeDark\n";

        Assert.IsTrue(OsThemePreference.PrefersDark("GNOME", null, gtkDark, null, null));
        Assert.IsFalse(OsThemePreference.PrefersDark("GNOME", null, gtkLight, gtkDark, kdeDark));
        Assert.IsTrue(OsThemePreference.PrefersDark("KDE", null, gtkLight, null, kdeDark));
        Assert.IsTrue(OsThemePreference.PrefersDark("GNOME", "Adwaita:dark", gtkLight, null, null));
        Assert.IsFalse(OsThemePreference.PrefersDark("GNOME", "Adwaita:light", gtkDark, null, kdeDark));
    }

    [TestMethod]
    public void Theme_WindowsAndMac_UsePlatformDefaultVariant()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Automatic variant on Linux depends on this machine's desktop portal.");
        }

        Assert.AreEqual(ThemeVariant.Default, OsThemePreference.AutomaticVariant());
    }
}
