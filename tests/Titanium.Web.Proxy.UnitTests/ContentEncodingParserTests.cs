using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Compression;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ContentEncodingParserTests
{
    [TestMethod]
    public void ParseContentEncodings_Single_ReturnsOne()
    {
        var result = CompressionUtil.ParseContentEncodings("gzip");
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("gzip", result[0]);
    }

    [TestMethod]
    public void ParseContentEncodings_Stacked_ReturnsAll()
    {
        var result = CompressionUtil.ParseContentEncodings("gzip, deflate");
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("gzip", result[0]);
        Assert.AreEqual("deflate", result[1]);
    }

    [TestMethod]
    public void ParseContentEncodings_Null_ReturnsEmpty()
    {
        var result = CompressionUtil.ParseContentEncodings(null);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseContentEncodings_Whitespace_HandledCorrectly()
    {
        var result = CompressionUtil.ParseContentEncodings("  br  , gzip ");
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("br", result[0]);
        Assert.AreEqual("gzip", result[1]);
    }
}
