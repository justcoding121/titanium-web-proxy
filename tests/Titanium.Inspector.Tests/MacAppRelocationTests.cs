using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MacAppRelocationTests
{
    [TestMethod]
    public void TryFindAppBundlePath_WhenNotInBundle_ReturnsNullOrExistingApp()
    {
        // Unit-test host is not inside a .app; method must not throw.
        var path = MacAppRelocation.TryFindAppBundlePath();
        if (path is not null)
            Assert.IsTrue(path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ShouldOfferMove_WhenDismissed_ReturnsFalse()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        var settings = new InspectorSettings { MacDmgMovePromptDismissed = true };
        Assert.IsFalse(MacAppRelocation.ShouldOfferMove(settings));
    }
}
