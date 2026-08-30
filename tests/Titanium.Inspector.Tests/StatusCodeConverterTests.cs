using System.Globalization;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Converters;

namespace Titanium.Inspector.Tests;

[TestClass]
public class StatusCodeConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [TestMethod]
    [DataRow(null, FontWeight.Normal)]
    [DataRow(100, FontWeight.Normal)]
    [DataRow(200, FontWeight.Normal)]
    [DataRow(204, FontWeight.Normal)]
    [DataRow(301, FontWeight.Normal)]
    [DataRow(304, FontWeight.Normal)]
    [DataRow(0, FontWeight.Normal)]
    [DataRow(404, FontWeight.SemiBold)]
    [DataRow(418, FontWeight.SemiBold)]
    [DataRow(500, FontWeight.SemiBold)]
    [DataRow(503, FontWeight.SemiBold)]
    public void FontWeightConverter_EmphasizesClientAndServerErrors(int? statusCode, FontWeight expected)
    {
        var result = StatusCodeFontWeightConverter.Instance.Convert(statusCode, typeof(FontWeight), null, Culture);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void FontWeightConverter_AcceptsBoxedInt()
    {
        object boxed = 502;
        var result = StatusCodeFontWeightConverter.Instance.Convert(boxed, typeof(FontWeight), null, Culture);
        Assert.AreEqual(FontWeight.SemiBold, result);
    }

    [TestMethod]
    public void BrushConverter_UsesDistinctFallbackColorsPerClass()
    {
        var pending = AssertSolid(StatusCodeBrushConverter.Instance.Convert(null, typeof(IBrush), null, Culture));
        var success = AssertSolid(StatusCodeBrushConverter.Instance.Convert(200, typeof(IBrush), null, Culture));
        var redirect = AssertSolid(StatusCodeBrushConverter.Instance.Convert(301, typeof(IBrush), null, Culture));
        var client = AssertSolid(StatusCodeBrushConverter.Instance.Convert(404, typeof(IBrush), null, Culture));
        var server = AssertSolid(StatusCodeBrushConverter.Instance.Convert(500, typeof(IBrush), null, Culture));
        var other = AssertSolid(StatusCodeBrushConverter.Instance.Convert(0, typeof(IBrush), null, Culture));

        Assert.AreEqual(Color.Parse("#888888"), pending.Color);
        Assert.AreEqual(Color.Parse("#0F7B0F"), success.Color);
        Assert.AreEqual(Color.Parse("#0078D4"), redirect.Color);
        Assert.AreEqual(Color.Parse("#C19C00"), client.Color);
        Assert.AreEqual(Color.Parse("#C42B1C"), server.Color);
        Assert.AreEqual(pending.Color, other.Color);
        Assert.AreEqual(pending.Color, AssertSolid(StatusCodeBrushConverter.Instance.Convert(101, typeof(IBrush), null, Culture)).Color);
    }

    [TestMethod]
    public void BrushConverter_AcceptsBoxedInt()
    {
        object boxed = 204;
        var brush = AssertSolid(StatusCodeBrushConverter.Instance.Convert(boxed, typeof(IBrush), null, Culture));
        Assert.AreEqual(Color.Parse("#0F7B0F"), brush.Color);
    }

    private static SolidColorBrush AssertSolid(object? value)
    {
        Assert.IsInstanceOfType<SolidColorBrush>(value);
        return (SolidColorBrush)value!;
    }
}
