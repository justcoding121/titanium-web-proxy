using System.Globalization;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Converters;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class BodyInspectNoticeBrushConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [TestMethod]
    public void Convert_None_ReturnsNull()
    {
        var result = BodyInspectNoticeBrushConverter.Instance.Convert(
            BodyInspectNoticeSeverity.None, typeof(IBrush), null, Culture);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Convert_InvalidValue_TreatedAsNone()
    {
        var result = BodyInspectNoticeBrushConverter.Instance.Convert("not-a-severity", typeof(IBrush), null, Culture);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Convert_WarningAndError_UseFallbackColorsWhenNoApp()
    {
        var warning = AssertSolid(BodyInspectNoticeBrushConverter.Instance.Convert(
            BodyInspectNoticeSeverity.Warning, typeof(IBrush), null, Culture));
        var error = AssertSolid(BodyInspectNoticeBrushConverter.Instance.Convert(
            BodyInspectNoticeSeverity.Error, typeof(IBrush), null, Culture));

        Assert.AreEqual(Color.Parse("#7A5200"), warning.Color);
        Assert.AreEqual(Color.Parse("#A4262C"), error.Color);
    }

    [TestMethod]
    public void ConvertBack_Throws()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            BodyInspectNoticeBrushConverter.Instance.ConvertBack(null, typeof(BodyInspectNoticeSeverity), null, Culture));
    }

    private static SolidColorBrush AssertSolid(object? value)
    {
        Assert.IsInstanceOfType<SolidColorBrush>(value);
        return (SolidColorBrush)value!;
    }
}
