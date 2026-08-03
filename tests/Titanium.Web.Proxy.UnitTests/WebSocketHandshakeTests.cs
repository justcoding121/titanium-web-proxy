using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketHandshakeTests
{
    [TestMethod]
    public void ComputeAccept_MatchesRfc6455Example()
    {
        // RFC 6455 §1.3 worked example.
        var accept = WebSocketHandshake.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ==");
        Assert.AreEqual("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", accept);
    }

    [TestMethod]
    public void ComputeAccept_NullOrEmpty_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => WebSocketHandshake.ComputeAccept(null!));
        Assert.ThrowsExactly<ArgumentException>(() => WebSocketHandshake.ComputeAccept(""));
    }
}
