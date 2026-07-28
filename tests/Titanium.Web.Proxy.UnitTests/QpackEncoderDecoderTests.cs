using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for the static-only QPACK encoder (<see cref="QpackEncoder" />) and
///     decoder (<see cref="QpackDecoder" />), verifying round-trip correctness for request
///     pseudo-headers, regular headers, and several edge cases.
/// </summary>
[TestClass]
public class QpackEncoderDecoderTests
{
    private static List<(string Name, string Value)> RoundTrip(List<(string, string)> headers)
    {
        var encoded = QpackEncoder.Encode(headers);
        return QpackDecoder.Decode(encoded.AsSpan());
    }

    [TestMethod]
    public void RoundTrip_EmptyHeaderList_ReturnsEmpty()
    {
        var result = RoundTrip([]);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void RoundTrip_SinglePseudoHeader_StatusOK()
    {
        var result = RoundTrip([(":status", "200")]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(":status", result[0].Name);
        Assert.AreEqual("200", result[0].Value);
    }

    [TestMethod]
    public void RoundTrip_RequestPseudoHeaders_PreservesAllValues()
    {
        var headers = new List<(string, string)>
        {
            (":method", "GET"),
            (":scheme", "https"),
            (":authority", "example.com"),
            (":path", "/index.html")
        };

        var result = RoundTrip(headers);

        Assert.AreEqual(4, result.Count);
        CollectionAssert.AreEqual(
            headers.Select(h => h.Item1).ToList(),
            result.Select(h => h.Name).ToList());
        CollectionAssert.AreEqual(
            headers.Select(h => h.Item2).ToList(),
            result.Select(h => h.Value).ToList());
    }

    [TestMethod]
    public void RoundTrip_CommonHeaders_ContentType()
    {
        var result = RoundTrip([(":status", "200"), ("content-type", "application/json")]);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("content-type", result[1].Name);
        Assert.AreEqual("application/json", result[1].Value);
    }

    [TestMethod]
    public void RoundTrip_StaticTableHit_AcceptEncoding()
    {
        // "accept-encoding: gzip, deflate, br" has a static table entry in QPACK.
        var result = RoundTrip([("accept-encoding", "gzip, deflate, br")]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("accept-encoding", result[0].Name);
        Assert.AreEqual("gzip, deflate, br", result[0].Value);
    }

    [TestMethod]
    public void RoundTrip_ArbitraryCustomHeader_Literal()
    {
        var result = RoundTrip([("x-custom-header", "my-value-123")]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("x-custom-header", result[0].Name);
        Assert.AreEqual("my-value-123", result[0].Value);
    }

    [TestMethod]
    public void RoundTrip_EmptyHeaderValue_PreservesEmptyString()
    {
        var result = RoundTrip([("x-empty", "")]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("x-empty", result[0].Name);
        Assert.AreEqual("", result[0].Value);
    }

    [TestMethod]
    public void RoundTrip_MultipleHeaders_PreservesOrder()
    {
        var headers = new List<(string, string)>
        {
            ("a", "1"),
            ("b", "2"),
            ("c", "3")
        };

        var result = RoundTrip(headers);

        Assert.AreEqual(3, result.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            Assert.AreEqual(headers[i].Item1, result[i].Name);
            Assert.AreEqual(headers[i].Item2, result[i].Value);
        }
    }
}
