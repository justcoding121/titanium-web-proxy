using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginGoAwayExceptionTests
{
    [TestMethod]
    public void Http2OriginGoAwayException_IsIOException_WithMessage()
    {
        var ex = new Http2OriginGoAwayException("GOAWAY before stream processed");
        Assert.IsInstanceOfType(ex, typeof(IOException));
        Assert.AreEqual("GOAWAY before stream processed", ex.Message);
    }
}
