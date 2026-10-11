using System.Globalization;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Locks the session status, feedback, link, and error brushes at WCAG AA (4.5:1)
/// against the window, row-hover, and selected-row backgrounds in both themes.
/// </summary>
[TestClass]
public class StatusContrastTests
{
    private const double MinimumContrast = 4.5;

    private static readonly string[] ForegroundKeys =
    [
        "SessionStatusSuccessBrush",
        "SessionStatusRedirectBrush",
        "SessionStatusClientErrorBrush",
        "SessionStatusServerErrorBrush",
        "SessionStatusMutedBrush",
        "StatusFeedbackNeutralBrush",
        "StatusFeedbackBusyBrush",
        "StatusFeedbackSuccessBrush",
        "StatusFeedbackWarningBrush",
        "StatusFeedbackErrorBrush",
        "InspectorLinkBrush",
        "InspectorErrorTextBrush",
    ];

    [TestMethod]
    public void LightAndDarkBrushes_ContrastAtLeastFourPointFive()
    {
        var doc = XDocument.Load(FindAppAxaml());
        var light = ThemeColors(doc, "Light");
        var dark = ThemeColors(doc, "Dark");

        AssertTheme(light, window: "#FFFFFF", name: "Light");
        var region = PaletteColor(doc, "Dark", "RegionColor");
        Assert.AreEqual("#1E1E1E", region, "Dark window background the status text sits on");
        AssertTheme(dark, window: region, name: "Dark");

        Assert.AreEqual("SemiBold", ThemeString(doc, "Light", "SessionStatusBaseFontWeight"));
        Assert.AreEqual("Normal", ThemeString(doc, "Dark", "SessionStatusBaseFontWeight"));
    }

    private static void AssertTheme(Dictionary<string, string> theme, string window, string name)
    {
        var surfaces = new[]
        {
            ("window", window),
            ("hover", theme["SessionGridHoverBrush"]),
            ("selection", theme["SessionGridSelectionBrush"]),
        };

        foreach (var key in ForegroundKeys)
        {
            Assert.IsTrue(theme.ContainsKey(key), $"{name} missing {key}");
            foreach (var (surfaceName, surface) in surfaces)
            {
                var ratio = Contrast(theme[key], surface);
                Assert.IsTrue(
                    ratio >= MinimumContrast,
                    $"{name} {key} {theme[key]} on {surfaceName} {surface} is {ratio:0.00}, need {MinimumContrast:0.00}");
            }
        }

        var selectionText = theme["SessionGridSelectionForegroundBrush"];
        var selectionRatio = Contrast(selectionText, theme["SessionGridSelectionBrush"]);
        Assert.IsTrue(
            selectionRatio >= MinimumContrast,
            $"{name} selection text {selectionText} on selection is {selectionRatio:0.00}");
    }

    private static Dictionary<string, string> ThemeColors(XDocument doc, string themeKey)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dict = doc.Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == themeKey);
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var brush in dict.Elements().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = (string?)brush.Attribute(x + "Key");
            var color = (string?)brush.Attribute("Color");
            if (key is not null && color is not null)
            {
                colors[key] = color;
            }
        }

        return colors;
    }

    private static string ThemeString(XDocument doc, string themeKey, string resourceKey)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dict = doc.Descendants()
            .First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == themeKey);
        var node = dict.Elements().First(e => (string?)e.Attribute(x + "Key") == resourceKey);
        return node.Value.Trim();
    }

    private static string PaletteColor(XDocument doc, string paletteKey, string attribute)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var palette = doc.Descendants()
            .First(e => e.Name.LocalName == "ColorPaletteResources" && (string?)e.Attribute(x + "Key") == paletteKey);
        return (string)palette.Attribute(attribute)!;
    }

    internal static double Contrast(string hexA, string hexB)
    {
        var a = Luminance(hexA);
        var b = Luminance(hexB);
        var hi = Math.Max(a, b);
        var lo = Math.Min(a, b);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex = hex.TrimStart('#');
        double Channel(int index)
        {
            var v = int.Parse(hex.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static string FindAppAxaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Titanium.Inspector", "App.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail("Could not locate src/Titanium.Inspector/App.axaml");
        return "";
    }
}
