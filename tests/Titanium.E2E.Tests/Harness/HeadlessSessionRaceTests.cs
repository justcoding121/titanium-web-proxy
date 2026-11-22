using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

[TestClass]
public class HeadlessSessionRaceTests
{
    [TestMethod]
    [TestCategory("E2E-UI")]
    public void LoadedQueueMutationIsRetriedLikeTheFontLocatorRace()
    {
        Assert.IsTrue(InspectorHeadlessFixture.IsTransientHeadlessFontRace(
            new InvalidOperationException("No service for type Avalonia.Media.IFontManagerImpl")));
        Assert.IsTrue(InspectorHeadlessFixture.IsTransientHeadlessFontRace(
            new InvalidOperationException("Collection was modified; enumeration operation may not execute.")));
        Assert.IsFalse(InspectorHeadlessFixture.IsTransientHeadlessFontRace(
            new InvalidOperationException("Call from invalid thread")));
    }
}
