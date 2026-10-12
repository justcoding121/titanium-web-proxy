using Avalonia.Controls;
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
    public const double BaselineHeight = 150;

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
        });
    }
}
