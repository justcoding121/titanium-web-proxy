using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3PseudoHeaderTests
{
    [TestMethod]
    public void DuplicateProtocolPseudoHeader_IsAMessageError()
    {
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
            Extract(new List<(string Name, string Value)>
            {
                (":protocol", "websocket"),
                (":protocol", "websocket"),
            }));

        Assert.IsInstanceOfType<Http3StreamException>(ex.InnerException);
        StringAssert.Contains(ex.InnerException!.Message, "Duplicate :protocol");
        Assert.AreEqual(Http3ErrorCode.MessageError, ((Http3StreamException)ex.InnerException).ErrorCode);
    }

    [TestMethod]
    public void OneProtocolPseudoHeader_IsReturnedForExtendedConnect()
    {
        var result = Extract(new List<(string Name, string Value)>
        {
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":authority", "127.0.0.1"),
            (":path", "/ws"),
        });

        Assert.AreEqual("websocket", result.GetType().GetField("Item5")!.GetValue(result));
        Assert.AreEqual("CONNECT", result.GetType().GetField("Item1")!.GetValue(result));
    }

    private static object Extract(List<(string Name, string Value)> fields)
    {
        var method = typeof(Http3RequestStream).GetMethod(
            "ExtractPseudoHeaders",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return method.Invoke(null, [fields])!;
    }
}
