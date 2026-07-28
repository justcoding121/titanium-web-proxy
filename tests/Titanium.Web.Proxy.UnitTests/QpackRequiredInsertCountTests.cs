using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Verifies the RFC 9204 §4.5.1.1 Required Insert Count encode/decode round-trip
///     implemented in <see cref="QpackEncoder.EncodeRequiredInsertCount" /> and
///     <see cref="QpackDecoder.DecodeRequiredInsertCount" />.
/// </summary>
[TestClass]
public class QpackRequiredInsertCountTests
{
    // MaxTableCapacity = 64 → MaxEntries = 2 (boundary-case friendly)
    private const uint SmallCapacity = 64;
    private const ulong SmallMaxEntries = 2;

    // MaxTableCapacity = 4096 → MaxEntries = 128
    private const uint StandardCapacity = 4096;

    // --- Encoding tests (RFC 9204 §4.5.1.1) ---

    [TestMethod]
    public void EncodeRequiredInsertCount_WhenZero_ReturnsPlusOne()
    {
        // RIC=0 is the "static-only" sentinel and should not be encoded via this path,
        // but the formula (0 % 2*MaxEntries) + 1 = 1 is well-defined.
        var encoded = QpackEncoder.EncodeRequiredInsertCount(0, SmallCapacity);
        Assert.AreEqual(1UL, encoded);
    }

    [TestMethod]
    public void EncodeRequiredInsertCount_RIC1_Capacity64()
    {
        // (1 % 4) + 1 = 2
        var encoded = QpackEncoder.EncodeRequiredInsertCount(1, SmallCapacity);
        Assert.AreEqual(2UL, encoded);
    }

    [TestMethod]
    public void EncodeRequiredInsertCount_RICEqualsMaxEntries_Capacity64()
    {
        // MaxEntries = 2; (2 % 4) + 1 = 3
        var encoded = QpackEncoder.EncodeRequiredInsertCount(SmallMaxEntries, SmallCapacity);
        Assert.AreEqual(3UL, encoded);
    }

    [TestMethod]
    public void EncodeRequiredInsertCount_RICEqualsMaxEntriesPlusOne_Capacity64()
    {
        // MaxEntries = 2; (3 % 4) + 1 = 4
        var encoded = QpackEncoder.EncodeRequiredInsertCount(SmallMaxEntries + 1, SmallCapacity);
        Assert.AreEqual(4UL, encoded);
    }

    [TestMethod]
    public void EncodeRequiredInsertCount_RICWrapsAroundFullRange_Capacity64()
    {
        // MaxEntries = 2, FullRange = 4; (4 % 4) + 1 = 1 (wraps)
        var encoded = QpackEncoder.EncodeRequiredInsertCount(2 * SmallMaxEntries, SmallCapacity);
        Assert.AreEqual(1UL, encoded);
    }

    [TestMethod]
    public void EncodeRequiredInsertCount_LargeRIC_StandardCapacity()
    {
        // MaxEntries = 128, FullRange = 256
        // RIC = 300: (300 % 256) + 1 = 44 + 1 = 45
        var encoded = QpackEncoder.EncodeRequiredInsertCount(300, StandardCapacity);
        Assert.AreEqual(45UL, encoded);
    }

    // --- Decoding tests (RFC 9204 §4.5.1.1 reverse) ---

    [TestMethod]
    public void DecodeRequiredInsertCount_WhenEncodedIsZero_ReturnsZero()
    {
        // encodedRic = 0 is the static-only sentinel.
        var decoded = QpackDecoder.DecodeRequiredInsertCount(0, 0, SmallCapacity);
        Assert.AreEqual(0UL, decoded);
    }

    [TestMethod]
    public void DecodeRequiredInsertCount_MaxTableCapacityZero_ReturnsZero()
    {
        var decoded = QpackDecoder.DecodeRequiredInsertCount(1, 0, 0);
        Assert.AreEqual(0UL, decoded);
    }

    [TestMethod]
    public void DecodeRequiredInsertCount_RIC1_Capacity64_RoundTrip()
    {
        const ulong ric = 1;
        var encoded = QpackEncoder.EncodeRequiredInsertCount(ric, SmallCapacity);
        // Decoder is called when insertCount >= ric - 1 (e.g. insertCount = ric - 1 = 0)
        var decoded = QpackDecoder.DecodeRequiredInsertCount(encoded, 0, SmallCapacity);
        Assert.AreEqual(ric, decoded, $"RIC round-trip failed for RIC={ric}");
    }

    [DataTestMethod]
    [DataRow(1UL)]
    [DataRow(2UL)]    // MaxEntries - 1
    [DataRow(3UL)]    // MaxEntries
    [DataRow(4UL)]    // MaxEntries + 1
    [DataRow(5UL)]
    [DataRow(8UL)]
    public void DecodeRequiredInsertCount_SmallCapacity_RoundTrip(ulong ric)
    {
        // insertCount is the current table insert count at decode time;
        // must be in range [RIC - MaxEntries, RIC + MaxEntries) per RFC 9204.
        ulong insertCount = ric > SmallMaxEntries ? ric - SmallMaxEntries : 0;

        var encoded = QpackEncoder.EncodeRequiredInsertCount(ric, SmallCapacity);
        var decoded = QpackDecoder.DecodeRequiredInsertCount(encoded, insertCount, SmallCapacity);

        Assert.AreEqual(ric, decoded, $"RIC={ric}: encoded={encoded}, insertCount={insertCount}");
    }

    [DataTestMethod]
    [DataRow(1UL)]
    [DataRow(127UL)]   // MaxEntries - 1
    [DataRow(128UL)]   // MaxEntries
    [DataRow(129UL)]   // MaxEntries + 1
    [DataRow(255UL)]
    [DataRow(256UL)]
    [DataRow(300UL)]
    public void DecodeRequiredInsertCount_StandardCapacity_RoundTrip(ulong ric)
    {
        const ulong maxEntries = 128;
        ulong insertCount = ric > maxEntries ? ric - maxEntries : 0;

        var encoded = QpackEncoder.EncodeRequiredInsertCount(ric, StandardCapacity);
        var decoded = QpackDecoder.DecodeRequiredInsertCount(encoded, insertCount, StandardCapacity);

        Assert.AreEqual(ric, decoded, $"RIC={ric}: encoded={encoded}, insertCount={insertCount}");
    }

    // --- Full encode/decode round-trip through the encoder/decoder API ---

    [TestMethod]
    public async Task EncodeDecodeWithContext_DynamicTableReference_RoundTrip()
    {
        await using var ctx = new QpackContext(SmallCapacity);
        ctx.MaxTableCapacityFromPeer = SmallCapacity;

        // Insert an entry into the outbound table so the encoder can reference it.
        ctx.OutboundEncoderTable.Insert("x-custom", "hello");

        var headers = new List<(string, string)>
        {
            ("x-custom", "hello")
        };

        var encoded = QpackEncoder.Encode(headers, ctx);

        // The RIC prefix should be non-zero (encodes a dynamic reference).
        Assert.AreNotEqual(0, encoded[0], "RIC byte should be non-zero for dynamic table reference.");

        // Decode: inbound table must have the same entry so the reference resolves.
        await using var decodeCtx = new QpackContext(SmallCapacity);
        decodeCtx.MaxTableCapacityFromPeer = SmallCapacity;
        decodeCtx.InboundDecoderTable.Insert("x-custom", "hello");
        decodeCtx.NotifyInsert();

        using var cts = new CancellationTokenSource(1000);
        var decoded = await QpackDecoder.DecodeAsync(new ReadOnlyMemory<byte>(encoded), decodeCtx, cts.Token);

        Assert.AreEqual(1, decoded.Count);
        Assert.AreEqual("x-custom", decoded[0].Name);
        Assert.AreEqual("hello", decoded[0].Value);
    }
}
