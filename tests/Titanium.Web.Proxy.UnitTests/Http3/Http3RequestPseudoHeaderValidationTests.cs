#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests.Http3;

/// <summary>
///     RFC 9114 §4.3 request field-section validation on the client-facing HTTP/3 request stream. A malformed
///     section must reset only that stream with H3_MESSAGE_ERROR rather than be resolved last-wins.
/// </summary>
[TestClass]
public class Http3RequestPseudoHeaderValidationTests
{
    private static readonly MethodInfo Extract = typeof(Http3RequestStream).GetMethod(
        "ExtractPseudoHeaders", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Http3StreamException ExpectRejected(params (string Name, string Value)[] fields)
    {
        var ex = Assert.ThrowsExactly<TargetInvocationException>(
            () => Extract.Invoke(null, [new List<(string Name, string Value)>(fields)]));
        return (Http3StreamException)ex.InnerException!;
    }

    [TestMethod]
    [DataRow(":method")]
    [DataRow(":scheme")]
    [DataRow(":authority")]
    [DataRow(":path")]
    [DataRow(":protocol")]
    public void DuplicatePseudoHeader_IsMessageError(string pseudo)
    {
        // Bug class: a repeated pseudo-header is resolved last-wins, so this hop forwards a different target than
        // an upstream that honours the first (request smuggling / cache poisoning).
        var ex = ExpectRejected(
            (":method", "GET"), (pseudo, "first"), (pseudo, "second"), (":authority", "a.example"), (":path", "/"));

        Assert.AreEqual(Http3ErrorCode.MessageError, ex.ErrorCode);
        StringAssert.Contains(ex.Message, pseudo);
    }

    [TestMethod]
    public void PseudoHeaderAfterRegularField_IsMessageError()
    {
        // Bug class: ordering violations are accepted, letting a client smuggle ":path" behind regular headers
        // that an intermediary already consumed.
        var ex = ExpectRejected(
            (":method", "GET"), (":authority", "a.example"), ("accept", "*/*"), (":path", "/late"));

        Assert.AreEqual(Http3ErrorCode.MessageError, ex.ErrorCode);
        StringAssert.Contains(ex.Message, ":path");
    }

    [TestMethod]
    public void WellFormedSection_StillParses_WithRegularFieldsInOrder()
    {
        var result = ((string?, string?, string?, string?, string?, List<(string, string)>))Extract.Invoke(
            null,
            [new List<(string Name, string Value)>
            {
                (":method", "GET"), (":scheme", "https"), (":authority", "a.example"), (":path", "/p"),
                ("accept", "*/*"), ("x-dup", "1"), ("x-dup", "2"),
            }])!;

        Assert.AreEqual("GET", result.Item1);
        Assert.AreEqual("/p", result.Item4);
        CollectionAssert.AreEqual(
            new[] { ("accept", "*/*"), ("x-dup", "1"), ("x-dup", "2") },
            result.Item6.ToArray(), "repeated regular fields are legal and must be preserved in order");
    }
}
#pragma warning restore CA1416
