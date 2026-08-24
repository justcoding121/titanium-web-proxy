using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Extra QPACK encode/decode coverage for Sonar new-code gates: malformed blocks,
///     dynamic-table edges, EncodeRequest/EncodeResponse helpers, and StatusCodeString branches.
/// </summary>
[TestClass]
public class QpackCoverageTests
{
    private const uint TableCapacity = 4096;

    // ── Decoder: Required Insert Count / blocked-stream rejection ──────────────

    [TestMethod]
    public async System.Threading.Tasks.Task Decode_RequiredInsertCountNotSatisfied_ThrowsBlockedStreamsRejected()
    {
        await using var ctx = new QpackContext(TableCapacity);
        ctx.MaxTableCapacityFromPeer = TableCapacity;
        // Empty inbound table (InsertCount = 0); peer claims RIC=1.
        var encodedRic = QpackEncoder.EncodeRequiredInsertCount(1, TableCapacity);
        Assert.IsTrue(encodedRic < 256);
        var block = new byte[] { (byte)encodedRic, 0x00 };

        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(block, ctx));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "SETTINGS_QPACK_BLOCKED_STREAMS");
    }

    [TestMethod]
    public void Decode_MissingBaseField_Throws()
    {
        // RIC uses 8-bit prefix overflow (0xFF) + one continuation byte that completes the int,
        // leaving no bytes for the Base field.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0xFF, 0x00 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Missing Base");
    }

    [TestMethod]
    public void Decode_InvalidRequiredInsertCount_Throws()
    {
        // Prefixed int starts overflow but never terminates.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0xFF, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid Required Insert Count");
    }

    [TestMethod]
    public void Decode_InvalidDeltaBase_Throws()
    {
        // RIC=0, then Delta Base with unterminated multi-byte integer (7-bit prefix max).
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x7F, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid Delta Base");
    }

    // ── Decoder: invalid static / dynamic index references ─────────────────────

    [TestMethod]
    public void Decode_StaticNameIndexOutOfRange_Throws()
    {
        // Literal with static name ref: 0 1 N=0 S=1 Index(4). Index 99 overflows 4-bit prefix.
        // 0x50 | 0x0F = 0x5F, then 99 - 15 = 84.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x5F, 84, 0x01, (byte)'x' }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Static table name index");
    }

    [TestMethod]
    public void Decode_DynamicIndexedWithoutEntry_Throws()
    {
        // Indexed Header Field, dynamic (S=0): 10xxxxxx — index 0 with no context.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Dynamic table absolute index");
    }

    [TestMethod]
    public void Decode_DynamicNameRefWithoutEntry_Throws()
    {
        // Literal with dynamic name ref (S=0): 0100xxxx — index 0, then value "x".
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x40, 0x01, (byte)'x' }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Dynamic table name index");
    }

    [TestMethod]
    public void Decode_InvalidIndexedFieldIndex_Throws()
    {
        // Indexed static with truncated multi-byte index.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0xFF, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid indexed field index");
    }

    [TestMethod]
    public void Decode_PostBaseNameRefWithoutEntry_Throws()
    {
        // Post-base literal name ref: 0000xxxx — index 0, value "v".
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x00, 0x01, (byte)'v' }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Post-base dynamic name index");
    }

    [TestMethod]
    public void Decode_InvalidPostBaseIndex_Throws()
    {
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x1F, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid post-base index");
    }

    [TestMethod]
    public void Decode_InvalidPostBaseNameRef_Throws()
    {
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x07, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid post-base name ref");
    }

    // ── Decoder: truncated literals / bad Huffman ──────────────────────────────

    [TestMethod]
    public void Decode_TruncatedLiteralName_Throws()
    {
        // Literal with literal name: 001 H=0, name length 5 but only 2 bytes follow.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x25, (byte)'a', (byte)'b' }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Truncated literal name");
    }

    [TestMethod]
    public void Decode_InvalidLiteralNameLength_Throws()
    {
        // Name length uses 3-bit prefix overflow that never terminates.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x27, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid literal name length");
    }

    [TestMethod]
    public void Decode_InvalidHuffmanLiteralName_Throws()
    {
        // H=1 on name: 0x00 is not valid Huffman padding (trailing bits must be all 1s).
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x29, 0x00, 0x01, (byte)'v' }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid Huffman-coded literal name");
    }

    [TestMethod]
    public void Decode_HuffmanEosInLiteralName_Throws()
    {
        // Four 0xFF bytes decode the EOS symbol mid-stream → HuffmanDecoder throws.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[]
            {
                0x00, 0x00, 0x2C, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, (byte)'v'
            }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid Huffman-coded literal name");
    }

    [TestMethod]
    public void Decode_InvalidLiteralValue_Throws()
    {
        // Static name ref for ":authority" (index 0), then truncated value length.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x50, 0x05 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid literal value");
    }

    [TestMethod]
    public void Decode_InvalidNameRefIndex_Throws()
    {
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x5F, 0x80 }));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid name ref index");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Decode_InvalidPostBaseLiteralValue_Throws()
    {
        await using var ctx = new QpackContext(TableCapacity);
        ctx.InboundDecoderTable.Insert("x-dyn", "old");
        // Post-base name ref index 0 resolves; value claims length 5 with no payload.
        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(new byte[] { 0x00, 0x00, 0x00, 0x05 }, ctx));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Invalid post-base literal value");
    }

    // ── Decoder: dynamic table success paths ───────────────────────────────────

    [TestMethod]
    public async System.Threading.Tasks.Task Decode_DynamicIndexedWithContext_Succeeds()
    {
        await using var ctx = new QpackContext(TableCapacity);
        ctx.MaxTableCapacityFromPeer = TableCapacity;
        ctx.InboundDecoderTable.Insert("x-dyn", "value-1");

        // Indexed dynamic (S=0): 10xxxxxx — absolute index 0.
        var block = new byte[] { 0x00, 0x00, 0x80 };
        var headers = QpackDecoder.Decode(block, ctx);
        Assert.AreEqual(1, headers.Count);
        Assert.AreEqual("x-dyn", headers[0].Name);
        Assert.AreEqual("value-1", headers[0].Value);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Decode_DynamicNameRefAndPostBase_Succeed()
    {
        await using var ctx = new QpackContext(TableCapacity);
        ctx.InboundDecoderTable.Insert("x-dyn", "seed");

        // Literal with dynamic name ref (S=0): 0x40 | 0, value "new"
        var literalDynName = new byte[] { 0x00, 0x00, 0x40, 0x03, (byte)'n', (byte)'e', (byte)'w' };
        var h1 = QpackDecoder.Decode(literalDynName, ctx);
        Assert.AreEqual(("x-dyn", "new"), h1[0]);

        // Post-base indexed: 0x10 | 0
        var postBase = new byte[] { 0x00, 0x00, 0x10 };
        var h2 = QpackDecoder.Decode(postBase, ctx);
        Assert.AreEqual(("x-dyn", "seed"), h2[0]);

        // Post-base literal name ref: 0x00 | 0, value "pb"
        var postBaseLit = new byte[] { 0x00, 0x00, 0x00, 0x02, (byte)'p', (byte)'b' };
        var h3 = QpackDecoder.Decode(postBaseLit, ctx);
        Assert.AreEqual(("x-dyn", "pb"), h3[0]);
    }

    // ── Encoder: uppercase names, EncodeRequest URI path, status codes ─────────

    [TestMethod]
    public void Encode_UppercaseHeaderNames_AreLowered()
    {
        var request = new Request
        {
            Method = "GET",
            Authority = "example.com".GetByteString(),
            RequestUriString8 = "/".GetByteString()
        };
        request.Headers.AddHeader("X-Custom-Header", "Abc");
        request.Headers.AddHeader("Content-Type", "text/plain");

        var encoded = QpackEncoder.EncodeRequest(request, "fallback.example");
        var decoded = QpackDecoder.Decode(encoded);

        Assert.IsTrue(decoded.Any(h => h.Name == "x-custom-header" && h.Value == "Abc"));
        Assert.IsTrue(decoded.Any(h => h.Name == "content-type"));
        Assert.IsFalse(decoded.Any(h => h.Name.Any(char.IsUpper)));
    }

    [TestMethod]
    public void EncodeResponse_UppercaseHeaderNames_AreLoweredWhenNotNormalized()
    {
        var response = new Response
        {
            StatusCode = 200,
            HeaderNamesAreHttp2Normalized = false
        };
        response.Headers.AddHeader("X-Trace-Id", "t1");
        response.Headers.AddHeader("Connection", "keep-alive"); // hop-by-hop: stripped

        var encoded = QpackEncoder.EncodeResponse(response, context: null);
        var decoded = QpackDecoder.Decode(encoded);

        Assert.IsTrue(decoded.Any(h => h is { Name: ":status", Value: "200" }));
        Assert.IsTrue(decoded.Any(h => h is { Name: "x-trace-id", Value: "t1" }));
        Assert.IsFalse(decoded.Any(h => h.Name == "connection"));
    }

    [TestMethod]
    public void EncodeRequest_AbsoluteUri_UsesAuthorityAndPathAndQuery()
    {
        var request = new Request
        {
            Method = "GET",
            IsHttps = true,
            // Absolute-form target: scheme present → RequestUri.Authority / PathAndQuery win.
            RequestUriString8 = "https://from-uri.example:8443/api/items?q=1".GetByteString(),
            Authority = ByteString.Empty
        };

        var encoded = QpackEncoder.EncodeRequest(request, authorityHost: "should-not-win.example");
        var decoded = QpackDecoder.Decode(encoded);

        Assert.AreEqual("from-uri.example:8443",
            decoded.Single(h => h.Name == ":authority").Value);
        Assert.AreEqual("/api/items?q=1",
            decoded.Single(h => h.Name == ":path").Value);
        Assert.AreEqual("https", decoded.Single(h => h.Name == ":scheme").Value);
    }

    [TestMethod]
    public void EncodeRequest_FallsBackToAuthorityHost_WhenNoHostOrAuthority()
    {
        var request = new Request
        {
            Method = "GET",
            RequestUriString8 = "/only-path".GetByteString(),
            Authority = ByteString.Empty
        };

        var encoded = QpackEncoder.EncodeRequest(request, "fallback.host");
        var decoded = QpackDecoder.Decode(encoded);

        Assert.AreEqual("fallback.host", decoded.Single(h => h.Name == ":authority").Value);
        Assert.AreEqual("/only-path", decoded.Single(h => h.Name == ":path").Value);
    }

    [DataTestMethod]
    [DataRow(204, "204")]
    [DataRow(301, "301")]
    [DataRow(302, "302")]
    [DataRow(304, "304")]
    [DataRow(400, "400")]
    [DataRow(404, "404")]
    [DataRow(500, "500")]
    [DataRow(502, "502")]
    [DataRow(503, "503")]
    [DataRow(418, "418")] // non-standard → StatusCode.ToString()
    [DataRow(599, "599")]
    public void EncodeResponse_StatusCodeString_Branches(int statusCode, string expected)
    {
        var response = new Response { StatusCode = statusCode };
        var encoded = QpackEncoder.EncodeResponse(response, context: null);
        var decoded = QpackDecoder.Decode(encoded);

        Assert.AreEqual(expected, decoded.Single(h => h.Name == ":status").Value);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Encode_DynamicExactAndNameRef_WithContext()
    {
        await using var ctx = new QpackContext(TableCapacity);
        ctx.MaxTableCapacityFromPeer = TableCapacity;
        ctx.OutboundEncoderTable.Insert("x-custom", "hello");

        // Exact dynamic match → WriteDynamicIndexed
        var exact = QpackEncoder.Encode([("x-custom", "hello")], ctx);
        Assert.AreNotEqual(0, exact[0], "RIC should be non-zero for dynamic exact ref");

        // Name-only dynamic match → WriteLiteralWithDynamicNameRef
        var nameRef = QpackEncoder.Encode([("x-custom", "other-value")], ctx);
        Assert.AreNotEqual(0, nameRef[0], "RIC should be non-zero for dynamic name ref");

        await using var decodeCtx = new QpackContext(TableCapacity);
        decodeCtx.MaxTableCapacityFromPeer = TableCapacity;
        decodeCtx.InboundDecoderTable.Insert("x-custom", "hello");

        // Decode exact block: post-base indexed resolves to seed entry.
        var decodedExact = QpackDecoder.Decode(exact, decodeCtx);
        Assert.AreEqual(("x-custom", "hello"), decodedExact[0]);

        // Decode name-ref block: name from table, value from literal.
        var decodedName = QpackDecoder.Decode(nameRef, decodeCtx);
        Assert.AreEqual(("x-custom", "other-value"), decodedName[0]);
    }

    [TestMethod]
    public void Encode_SkipsHopByHopAndTeHeaders()
    {
        var request = new Request
        {
            Method = "GET",
            Host = "h.example",
            RequestUriString8 = "/".GetByteString()
        };
        request.Headers.AddHeader("TE", "trailers");
        request.Headers.AddHeader("Upgrade", "websocket");
        request.Headers.AddHeader("X-Keep", "1");

        var decoded = QpackDecoder.Decode(QpackEncoder.EncodeRequest(request, "unused"));
        Assert.IsFalse(decoded.Any(h => h.Name is "te" or "upgrade" or "host"));
        Assert.IsTrue(decoded.Any(h => h is { Name: "x-keep", Value: "1" }));
    }
}
