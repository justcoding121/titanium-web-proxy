using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public class HexVectorTests
{
    [TestMethod]
    public void ParseSkipsWhitespaceCommentsAnd0xPrefixes()
    {
        byte[] bytes = HexVectors.Parse("0x0a 0B\n# ignored ff\ncc");
        CollectionAssert.AreEqual(new byte[] { 0x0a, 0x0b, 0xcc }, bytes);
    }

    [TestMethod]
    public void ParseRejectsAnOddNumberOfDigits()
    {
        Assert.ThrowsException<System.FormatException>(() => HexVectors.Parse("abc"));
    }
}
