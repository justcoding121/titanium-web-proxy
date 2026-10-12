using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class AlwaysOnTopHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task AlwaysOnTop_SetsTheMainWindow()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var dialog = new Avalonia.Controls.Window();
            Assert.IsFalse(fx.Window.Topmost);
            Assert.IsFalse(dialog.Topmost);
            fx.ViewModel.AlwaysOnTop = true;
            fx.ViewModel.ApplyAlwaysOnTop(fx.Window);
            Assert.IsTrue(fx.Window.Topmost);
            Assert.IsFalse(dialog.Topmost);
            fx.ViewModel.AlwaysOnTop = false;
            fx.ViewModel.ApplyAlwaysOnTop(fx.Window);
            Assert.IsFalse(fx.Window.Topmost);
        });
    }
}
