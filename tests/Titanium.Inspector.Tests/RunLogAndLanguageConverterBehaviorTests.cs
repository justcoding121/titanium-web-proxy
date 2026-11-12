using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Converters;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Run-log level and language radio converters (pure, no Application theme required for weight/match).
/// </summary>
[TestClass]
public class RunLogAndLanguageConverterBehaviorTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [TestMethod]
    public void RunLogFontWeight_BoldsOnlyCritical()
    {
        Assert.AreEqual(FontWeight.Bold,
            RunLogLevelFontWeightConverter.Instance.Convert(LogLevel.Critical, typeof(FontWeight), null, Culture));
        Assert.AreEqual(FontWeight.Normal,
            RunLogLevelFontWeightConverter.Instance.Convert(LogLevel.Error, typeof(FontWeight), null, Culture));
        Assert.AreEqual(FontWeight.Normal,
            RunLogLevelFontWeightConverter.Instance.Convert(LogLevel.Information, typeof(FontWeight), null, Culture));
        Assert.AreEqual(FontWeight.Normal,
            RunLogLevelFontWeightConverter.Instance.Convert("not-a-level", typeof(FontWeight), null, Culture));
    }

    [TestMethod]
    public void RunLogBrush_UnsetWithoutApplicationTheme_AndRejectsNonLogLevel()
    {
        Assert.AreEqual(AvaloniaProperty.UnsetValue,
            RunLogLevelBrushConverter.Instance.Convert(null, typeof(IBrush), null, Culture));
        Assert.AreEqual(AvaloniaProperty.UnsetValue,
            RunLogLevelBrushConverter.Instance.Convert(LogLevel.Information, typeof(IBrush), null, Culture));
        Assert.AreEqual(AvaloniaProperty.UnsetValue,
            RunLogLevelBrushConverter.Instance.Convert(LogLevel.Error, typeof(IBrush), null, Culture));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            RunLogLevelBrushConverter.Instance.ConvertBack(null, typeof(LogLevel), null, Culture));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            RunLogLevelFontWeightConverter.Instance.ConvertBack(FontWeight.Bold, typeof(LogLevel), null, Culture));
    }

    [TestMethod]
    [DataRow("auto", "auto", true)]
    [DataRow("en", "EN", true)]
    [DataRow("zh-Hans", "zh-hans", true)]
    [DataRow("en", "fr", false)]
    [DataRow(null, "auto", false)]
    public void LanguageMatch_ComparesIgnoreCase(string? value, string? parameter, bool expected) =>
        Assert.AreEqual(expected, LanguageMatchConverter.Instance.Convert(value, typeof(bool), parameter, Culture));
}
