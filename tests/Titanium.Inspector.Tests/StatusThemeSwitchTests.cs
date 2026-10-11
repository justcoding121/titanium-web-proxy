using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Converters;

namespace Titanium.Inspector.Tests;

/// <summary>Proves status brushes follow ActualThemeVariant, including a convert that happens after a switch.</summary>
[TestClass]
public class StatusThemeSwitchTests
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [TestMethod]
    public async Task BrushFollowsThemeVariant_IncludingARowConvertedAfterTheSwitch()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(StatusThemeSwitchTests));
        await session.Dispatch(() =>
        {
            var app = Application.Current!;
            var converter = StatusCodeBrushConverter.Instance;
            var weight = StatusCodeFontWeightConverter.Instance;

            app.RequestedThemeVariant = ThemeVariant.Light;
            AssertColor(converter, 200, "#0A5F0A");
            AssertColor(converter, 304, "#005A9E");
            AssertColor(converter, 403, "#7A5200");
            AssertColor(converter, 500, "#A4262C");
            Assert.AreEqual(FontWeight.SemiBold, weight.Convert(200, typeof(FontWeight), null, Culture()));

            // Resolved before the switch. The grid rebinds via ThemeRefreshTick; the new convert must move.
            var beforeSwitch = AssertSolid(converter.Convert(200, typeof(IBrush), null, Culture()));

            app.RequestedThemeVariant = ThemeVariant.Dark;
            AssertColor(converter, 200, "#8ADB7A");
            AssertColor(converter, 304, "#4CC2FF");
            Assert.AreEqual(FontWeight.Normal, weight.Convert(200, typeof(FontWeight), null, Culture()));
            Assert.AreEqual(FontWeight.SemiBold, weight.Convert(403, typeof(FontWeight), null, Culture()));
            Assert.AreNotEqual(beforeSwitch.Color, AssertSolid(converter.Convert(200, typeof(IBrush), null, Culture())).Color);

            app.RequestedThemeVariant = ThemeVariant.Light;
            AssertColor(converter, 200, "#0A5F0A");
            Assert.AreEqual(FontWeight.SemiBold, weight.Convert(304, typeof(FontWeight), null, Culture()));

            app.RequestedThemeVariant = ThemeVariant.Default;
            var actual = app.ActualThemeVariant;
            var expected = actual == ThemeVariant.Dark ? "#8ADB7A" : "#0A5F0A";
            AssertColor(converter, 200, expected);
        }, CancellationToken.None);
    }

    private static void AssertColor(StatusCodeBrushConverter converter, int status, string hex)
    {
        var brush = AssertSolid(converter.Convert(status, typeof(IBrush), null, Culture()));
        Assert.AreEqual(Color.Parse(hex), brush.Color, $"status {status}");
    }

    private static SolidColorBrush AssertSolid(object? value)
    {
        Assert.IsInstanceOfType<SolidColorBrush>(value);
        return (SolidColorBrush)value!;
    }

    private static System.Globalization.CultureInfo Culture() => System.Globalization.CultureInfo.InvariantCulture;
}
