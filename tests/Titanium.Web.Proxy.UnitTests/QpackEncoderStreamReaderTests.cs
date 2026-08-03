using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for QPACK encoder-stream instruction parsing (RFC 9204 §3.2).
/// </summary>
[TestClass]
public class QpackEncoderStreamReaderTests
{
    [TestMethod]
    public async Task TryParse_SetDynamicTableCapacity_AppliesCapacity()
    {
        await using var ctx = new QpackContext(4096);
        // Set Capacity 100: 01 + 6-bit prefix. 100 > 63 → 0x7F then remainder 37.
        byte[] instruction = [0x7F, 37];

        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(instruction, ctx, out var consumed));
        Assert.AreEqual(2, consumed);
        Assert.AreEqual(100u, ctx.InboundDecoderTable.Capacity);
    }

    [TestMethod]
    public async Task TryParse_InsertWithLiteralName_InsertsEntry()
    {
        await using var ctx = new QpackContext(4096);
        var instruction = EncodeInsertLiteral("x-custom", "abc");

        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(instruction, ctx, out _));
        Assert.AreEqual(1UL, ctx.InboundDecoderTable.InsertCount);
        Assert.IsTrue(ctx.InboundDecoderTable.TryGetByAbsoluteIndex(0, out var name, out var value));
        Assert.AreEqual("x-custom", name);
        Assert.AreEqual("abc", value);
    }

    [TestMethod]
    public async Task TryParse_InsertWithStaticNameReference_UsesStaticTableName()
    {
        await using var ctx = new QpackContext(4096);
        // Static table index 25 is commonly ":method" / related — use index 17 ("content-type") if present.
        // Encode Insert With Name Reference, static, index that fits in 6 bits, value "text/plain".
        // Format: 1 S=1 T=0 Index(6) + value literal.
        // Index 25 (< 63): first byte = 0x80 | 0x40 | 25 = 0xD9
        var valueBytes = Encoding.Latin1.GetBytes("text/plain");
        var instruction = new byte[1 + 1 + valueBytes.Length];
        instruction[0] = 0xD9; // static name ref index 25
        instruction[1] = (byte)valueBytes.Length; // non-huffman length (fits in 7 bits)
        valueBytes.CopyTo(instruction.AsSpan(2));

        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(instruction, ctx, out _));
        Assert.AreEqual(1UL, ctx.InboundDecoderTable.InsertCount);
        Assert.IsTrue(ctx.InboundDecoderTable.TryGetByAbsoluteIndex(0, out var name, out var value));
        Assert.AreEqual("text/plain", value);
        Assert.IsFalse(string.IsNullOrEmpty(name));
    }

    [TestMethod]
    public async Task TryParse_Duplicate_ReinsertsExistingEntry()
    {
        await using var ctx = new QpackContext(4096);
        var insert = EncodeInsertLiteral("a", "1");
        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(insert, ctx, out _));

        // Duplicate absolute index 0: 000 Index(5) → first byte = 0x00
        byte[] dup = [0x00];
        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(dup, ctx, out _));
        Assert.AreEqual(2UL, ctx.InboundDecoderTable.InsertCount);
    }

    [TestMethod]
    public async Task TryParse_IncompleteInstruction_ReturnsFalseWithoutConsuming()
    {
        await using var ctx = new QpackContext(4096);
        // Insert With Literal Name prefix but truncated name length.
        byte[] incomplete = [0x20, 0x05]; // 001 ...., length=5, no name bytes

        Assert.IsFalse(QpackEncoderStreamReader.TryParseOneInstruction(incomplete, ctx, out var consumed));
        Assert.AreEqual(0, consumed);
        Assert.AreEqual(0UL, ctx.InboundDecoderTable.InsertCount);
    }

    [TestMethod]
    public async Task ProcessAsync_CarriesPendingAcrossReads_ThenApplies()
    {
        await using var ctx = new QpackContext(4096);
        var instruction = EncodeInsertLiteral("host", "example.com");

        // Feed one byte at a time via a stream that yields partial reads.
        await using var ms = new MemoryStream(instruction);
        await QpackEncoderStreamReader.ProcessAsync(ms, ctx, CancellationToken.None);

        Assert.AreEqual(1UL, ctx.InboundDecoderTable.InsertCount);
        Assert.IsTrue(ctx.InboundDecoderTable.TryGetByAbsoluteIndex(0, out var name, out var value));
        Assert.AreEqual("host", name);
        Assert.AreEqual("example.com", value);
    }

    [TestMethod]
    public async Task ProcessAsync_TruncatedInstructionAtEof_ThrowsQpackEncoderStreamError()
    {
        await using var ctx = new QpackContext(4096);
        await using var ms = new MemoryStream([0x20, 0x05]); // incomplete insert-literal

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => QpackEncoderStreamReader.ProcessAsync(ms, ctx, CancellationToken.None));

        Assert.AreEqual(Http3ErrorCode.QpackEncoderStreamError, ex.ErrorCode);
    }

    [TestMethod]
    public async Task TryParse_OutOfRangeStaticIndex_SkipsInsert()
    {
        await using var ctx = new QpackContext(4096);
        // Prefixed int with 6-bit mask all-ones then large remainder → index beyond static table.
        // First byte 0xFF (static + mask), then encode a large index remainder.
        // Value for nameIndex: mask=63, we want nameIndex = 5000.
        // remainder = 5000 - 63 = 4937. Encode 4937 as 7-bit chunks.
        var instruction = new byte[16];
        instruction[0] = 0xFF;
        var rem = 5000UL - 63;
        var i = 1;
        while (rem >= 0x80)
        {
            instruction[i++] = (byte)((rem & 0x7F) | 0x80);
            rem >>= 7;
        }
        instruction[i++] = (byte)rem;
        instruction[i++] = 0x00; // empty value literal

        Assert.IsTrue(QpackEncoderStreamReader.TryParseOneInstruction(instruction.AsSpan(0, i), ctx, out _));
        Assert.AreEqual(0UL, ctx.InboundDecoderTable.InsertCount);
    }

    private static byte[] EncodeInsertLiteral(string name, string value)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        var valueBytes = Encoding.Latin1.GetBytes(value);
        // 001 N=0 .... : first byte 0x20, then name literal (7-bit len), value literal (7-bit len)
        var buf = new byte[1 + 1 + nameBytes.Length + 1 + valueBytes.Length];
        buf[0] = 0x20;
        buf[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buf.AsSpan(2));
        buf[2 + nameBytes.Length] = (byte)valueBytes.Length;
        valueBytes.CopyTo(buf.AsSpan(3 + nameBytes.Length));
        return buf;
    }
}
