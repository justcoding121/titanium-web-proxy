using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests.UiHeadless;

/// <summary>
/// Pins the toolbar height at the 1280px baseline window so a new filter control
/// that wraps onto another row fails here instead of shifting every shell baseline.
/// </summary>
[TestClass]
public class ToolbarHeightBaselineTests
{
    /// <summary>Includes the Method and Status count pills, which keep their height even when idle.</summary>
    public const double BaselineHeight = 162;

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Toolbar_AtBaselineWidth_KeepsItsHeight()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            Assert.AreEqual(1280, fx.Window.Width, 0.1);
            Assert.IsTrue(fx.Robot.TryFind<StackPanel>("ToolbarPanel", out var panel) && panel is not null);
            var height = panel!.Bounds.Height;
            Assert.IsTrue(height > 0, "Toolbar did not layout.");
            Assert.AreEqual(
                BaselineHeight,
                height,
                1.0,
                $"Toolbar height is {height}. Pin BaselineHeight to this value when the layout is intended.");

            Assert.IsTrue(fx.Robot.TryFind<TextBox>("SearchBox", out var search) && search is not null);
            Assert.IsTrue(fx.Robot.TryFind<Border>("ToolbarFilterGroup", out var filters) && filters is not null);
            Assert.AreEqual(
                filters!.Bounds.Height,
                search!.Bounds.Height,
                1.0,
                $"Search box height {search.Bounds.Height} should match the Filters border {filters.Bounds.Height}.");
            Assert.IsFalse(search.AcceptsReturn);
            Assert.AreEqual(TextWrapping.Wrap, search.TextWrapping);
            Assert.AreEqual(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(search));
            Assert.AreEqual(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(search));

            fx.Window.Width = 360;
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(filters.Bounds.Y > search.Bounds.Y, "Filters border should wrap under the search box.");
            Assert.AreEqual(filters.Bounds.Height, search.Bounds.Height, 1.0);
            var gap = filters.Bounds.Y - search.Bounds.Bottom;
            Assert.IsTrue(gap >= 7, $"Wrap gap was {gap}.");
        });
    }
}
