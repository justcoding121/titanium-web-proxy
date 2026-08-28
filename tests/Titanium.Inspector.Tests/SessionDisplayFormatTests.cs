using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionDisplayFormatTests
{
    [TestMethod]
    public void FormatHttpProtocol_MapsKnownVersions()
    {
        Assert.AreEqual("?", SessionDisplayFormat.FormatHttpProtocol(null));
        Assert.AreEqual("?", SessionDisplayFormat.FormatHttpProtocol(new Version(0, 0)));
        Assert.AreEqual("h1.0", SessionDisplayFormat.FormatHttpProtocol(new Version(1, 0)));
        Assert.AreEqual("h1.1", SessionDisplayFormat.FormatHttpProtocol(new Version(1, 1)));
        Assert.AreEqual("h2", SessionDisplayFormat.FormatHttpProtocol(new Version(2, 0)));
        Assert.AreEqual("h3", SessionDisplayFormat.FormatHttpProtocol(new Version(3, 0)));
    }

    [TestMethod]
    public void FormatClientServer_UsesShortNamesAndArrow()
    {
        Assert.AreEqual("h1.1", SessionDisplayFormat.FormatClientServer(new Version(1, 1), null));
        Assert.AreEqual("h1.1 → h2", SessionDisplayFormat.FormatClientServer(new Version(1, 1), new Version(2, 0)));
        Assert.AreEqual("h2 → h3", SessionDisplayFormat.FormatClientServer(new Version(2, 0), new Version(3, 0)));
        Assert.AreEqual("h1.1 → h1.1", SessionDisplayFormat.FormatClientServer(new Version(1, 1), new Version(1, 1)));
    }
}
