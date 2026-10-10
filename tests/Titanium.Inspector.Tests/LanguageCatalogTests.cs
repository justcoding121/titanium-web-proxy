using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;

namespace Titanium.Inspector.Tests;

[TestClass]
[DoNotParallelize]
public class LanguageCatalogTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    [TestMethod]
    public void Resolve_MapsOsCulturesOntoTheCatalog()
    {
        Assert.AreEqual("zh-Hans", LanguageService.ResolveCultureName("zh-CN"));
        Assert.AreEqual("zh-Hant", LanguageService.ResolveCultureName("zh-TW"));
        Assert.AreEqual("zh-Hans", LanguageService.ResolveCultureName("zh"));
        Assert.AreEqual("pt-BR", LanguageService.ResolveCultureName("pt"));
        Assert.AreEqual("pt-PT", LanguageService.ResolveCultureName("pt-PT"));
        Assert.AreEqual("nb", LanguageService.ResolveCultureName("nn"));
        Assert.AreEqual("nb", LanguageService.ResolveCultureName("no"));
        Assert.AreEqual("fr", LanguageService.ResolveCultureName("fr-FR"));
        Assert.AreEqual("en", LanguageService.ResolveCultureName("en-GB"));
        Assert.AreEqual("he", LanguageService.ResolveCultureName("iw"));
        Assert.AreEqual("en", LanguageService.ResolveCultureName("not-a-culture"));
    }

    [TestMethod]
    public void Load_DoesNotChangeThreadCulture_AndSetsFlowDirection()
    {
        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            LanguageService.Instance.Load("ar");
            Assert.AreEqual("ar", LanguageService.Instance.ActiveCulture);
            Assert.AreEqual(FlowDirection.RightToLeft, LanguageService.Instance.FlowDirection);
            Assert.AreEqual(culture, CultureInfo.CurrentCulture);
            Assert.AreEqual(ui, CultureInfo.CurrentUICulture);
            Assert.AreNotEqual("_File", LanguageService.Get("menu.file"));

            LanguageService.Instance.Load("en");
            Assert.AreEqual(FlowDirection.LeftToRight, LanguageService.Instance.FlowDirection);
            Assert.AreEqual("_File", LanguageService.Get("menu.file"));
        }
        finally
        {
            LanguageService.Instance.Load(LanguageService.English);
        }
    }

    [TestMethod]
    public void Catalogs_MatchEnglishKeys_Placeholders_AndAreNonEmpty()
    {
        var english = LanguageService.ReadCatalog(LanguageService.English);
        Assert.IsTrue(english.Count > 0, "English catalog is empty.");

        foreach (var culture in LanguageService.ShippedCultures)
        {
            var catalog = LanguageService.ReadCatalog(culture);
            Assert.IsTrue(catalog.Count > 0, culture + " catalog is missing.");

            var englishKeys = english.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var keys = catalog.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(englishKeys, keys, culture + " keys differ from English.");

            foreach (var key in englishKeys)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(catalog[key]), culture + " " + key + " is empty.");
                var expected = Placeholder.Matches(english[key]).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
                var actual = Placeholder.Matches(catalog[key]).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(expected, actual, culture + " " + key + " placeholders differ.");
            }
        }
    }
}
