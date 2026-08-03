using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
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

    [TestMethod]
    public void LiteralNewName_WireFormat_EmbedsNameLengthInPrefixByte()
    {
        // RFC 9204 §4.5.6: first instruction byte is 001 N H + 3-bit-prefixed name length.
        var shortName = "x-short"; // length 7 → fits in 3-bit prefix (max 7 before overflow)
        var encoded = QpackEncoder.Encode([(shortName, "v")]);
        Assert.IsTrue(encoded.Length > 2);
        var first = encoded[2];
        Assert.AreEqual(0x20, first & 0xF8, "top 5 bits must be 00100 (N=0,H=0)");
        Assert.AreEqual(shortName.Length, first & 0x07);

        // Longer names use the prefixed-integer overflow form (low 3 bits all 1).
        var longEncoded = QpackEncoder.Encode([("x-custom-header", "my-value-123")]);
        Assert.AreEqual(0x27, longEncoded[2] & 0xFF, "name length >= 7 sets 3-bit prefix to max");
    }

    [TestMethod]
    public void Decode_HuffmanEncodedLiteralNameAndValue_Succeeds()
    {
        // RIC=0, DeltaBase=0, then literal with literal name (001xxxxx) with Huffman bits set on name+value.
        using var nameMs = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(nameMs, System.Text.Encoding.ASCII, leaveOpen: true))
            Titanium.Web.Proxy.Http2.Hpack.HuffmanEncoder.Instance.Encode(w,
                new Titanium.Web.Proxy.Models.ByteString(System.Text.Encoding.ASCII.GetBytes("x-h")));
        var nameHuff = nameMs.ToArray();
        using var valueMs = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(valueMs, System.Text.Encoding.ASCII, leaveOpen: true))
            Titanium.Web.Proxy.Http2.Hpack.HuffmanEncoder.Instance.Encode(w,
                new Titanium.Web.Proxy.Models.ByteString(System.Text.Encoding.ASCII.GetBytes("v1")));
        var valueHuff = valueMs.ToArray();

        Assert.IsTrue(nameHuff.Length < 7);
        Assert.IsTrue(valueHuff.Length < 127);

        var encoded = new List<byte> { 0x00, 0x00 };
        // 001 N=0 H=1 + 3-bit name length
        encoded.Add((byte)(0x20 | 0x08 | nameHuff.Length));
        encoded.AddRange(nameHuff);
        // value: H=1 + 7-bit length
        encoded.Add((byte)(0x80 | valueHuff.Length));
        encoded.AddRange(valueHuff);

        var decoded = QpackDecoder.Decode(encoded.ToArray());
        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual("x-h", decoded[0].Name);
        Assert.AreEqual("v1", decoded[0].Value);
    }

    [TestMethod]
    public void Decode_PostBaseIndexedWithoutContext_Throws()
    {
        // RIC=0 DeltaBase=0, then post-base indexed (0001xxxx) index 0
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x10 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
    }

    [TestMethod]
    public void Decode_TooShort_ThrowsDecompressionFailed()
    {
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(() => QpackDecoder.Decode(new byte[] { 0x00 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
    }

    [TestMethod]
    public void Decode_NonZeroRicWithoutContext_Throws()
    {
        // Prefixed RIC=1 (byte 0x01) + Delta Base=0 (byte 0x00)
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x01, 0x00 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Required Insert Count");
    }

    [TestMethod]
    public void Decode_StaticIndexOutOfRange_Throws()
    {
        // RIC=0, DeltaBase=0, then indexed static with absurd index (0xFF with 6-bit prefix overflow)
        // Static indexed pattern: 11xxxxxx — use index far beyond table via 0xFF 0xFF
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0xFF, 0xFF }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task DecodeAsync_Cancellation_Throws()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<System.OperationCanceledException>(
            () => QpackDecoder.DecodeAsync(new byte[] { 0x00, 0x00 }, null, cts.Token));
    }
}
