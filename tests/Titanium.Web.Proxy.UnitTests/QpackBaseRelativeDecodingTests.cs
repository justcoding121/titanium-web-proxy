using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     RFC 9204 Base-relative and post-base dynamic-table decoding.
/// </summary>
[TestClass]
public class QpackBaseRelativeDecodingTests
{
    [TestMethod]
    public void Decode_StaticOnly_RequiredInsertCountZero_Unchanged()
    {
        var encoded = QpackEncoder.Encode([(":status", "200")]);
        var decoded = QpackDecoder.Decode(encoded.AsSpan());

        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual(":status", decoded[0].Name);
        Assert.AreEqual("200", decoded[0].Value);
    }

    [TestMethod]
    public void Decode_RelativeDynamicIndexed_BaseEqualsRic_ResolvesAbsoluteZero()
    {
        // RIC=1, S=0, ΔBase=0 → Base=1. Relative wireIndex=0 → abs = Base−1−0 = 0.
        var context = new QpackContext(4096);
        context.MaxTableCapacityFromPeer = 4096;
        context.InboundDecoderTable.Insert("x-custom", "one");

        // Encoded RIC for absolute count=1 with MaxEntries=128 is (1 % 256) + 1 = 2.
        var block = new byte[]
        {
            0x02, // Required Insert Count (encoded)
            0x00, // S=0, DeltaBase=0 → Base=1
            0x80  // Indexed dynamic, relative index 0
        };

        var decoded = QpackDecoder.Decode(block.AsMemory(), context);

        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual("x-custom", decoded[0].Name);
        Assert.AreEqual("one", decoded[0].Value);
    }

    [TestMethod]
    public void Decode_PostBaseIndexed_BaseZero_ResolvesAbsoluteZero()
    {
        // RIC=1, S=1, ΔBase=0 → Base = 1−(0+1) = 0. Post-base wireIndex=0 → abs=0.
        var context = new QpackContext(4096);
        context.MaxTableCapacityFromPeer = 4096;
        context.InboundDecoderTable.Insert("x-custom", "post");

        var block = new byte[]
        {
            0x02, // encoded RIC for absolute count 1
            0x80, // S=1, DeltaBase=0 → Base=0
            0x10  // Post-base indexed, wire index 0
        };

        var decoded = QpackDecoder.Decode(block.AsMemory(), context);

        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual("x-custom", decoded[0].Name);
        Assert.AreEqual("post", decoded[0].Value);
    }

    [TestMethod]
    public void Decode_RelativeIndex_WhenBaseIsZero_ThrowsQpackDecompressionFailed()
    {
        var context = new QpackContext(4096);
        context.MaxTableCapacityFromPeer = 4096;
        context.InboundDecoderTable.Insert("x-custom", "one");

        // Base=0 via S=1 ΔBase=0, but a relative (not post-base) dynamic index is out of range.
        var block = new byte[]
        {
            0x02,
            0x80,
            0x80 // relative dynamic index 0 with Base=0
        };

        var ex = Assert.ThrowsExactly<Http3ConnectionException>(
            () => QpackDecoder.Decode(block.AsMemory(), context));
        Assert.AreEqual(Http3ErrorCode.QpackDecompressionFailed, ex.ErrorCode);
    }

    [TestMethod]
    public void EncodeDecode_DynamicTable_RoundTrip()
    {
        var context = new QpackContext(4096);
        context.MaxTableCapacityFromPeer = 4096;

        // Seed outbound table so the encoder can reference the entry, then mirror into inbound
        // so the decoder can resolve the same absolute index.
        context.OutboundEncoderTable.Insert("x-roundtrip", "value");
        context.InboundDecoderTable.Insert("x-roundtrip", "value");

        var headers = new List<(string, string)> { ("x-roundtrip", "value") };
        var encoded = QpackEncoder.Encode(headers, context);
        var decoded = QpackDecoder.Decode(encoded.AsMemory(), context);

        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual("x-roundtrip", decoded[0].Name);
        Assert.AreEqual("value", decoded[0].Value);
    }
}
