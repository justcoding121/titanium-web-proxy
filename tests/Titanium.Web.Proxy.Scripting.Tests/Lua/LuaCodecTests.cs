using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
/// Unit tests for <see cref="LuaWriter"/> and <see cref="LuaReader"/>.
/// These tests require no native plugin runtime.
/// </summary>
[TestClass]
public class LuaCodecTests
{
    // -------------------------------------------------------------------------------------
    // Round-trip helpers
    // -------------------------------------------------------------------------------------

    private static byte[] Encode(Action<LuaWriter> write)
    {
        var w = new LuaWriter();
        write(w);
        return w.ToArray();
    }

    private static object? Decode(byte[] bytes)
    {
        var r = new LuaReader(bytes);
        return r.Read();
    }

    private static object? RoundTrip(Action<LuaWriter> write)
    {
        return Decode(Encode(write));
    }

    // -------------------------------------------------------------------------------------
    // Round-trip: scalars
    // -------------------------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_Null()
    {
        Assert.IsNull(RoundTrip(w => w.Nil()));
    }

    [TestMethod]
    public void RoundTrip_BoolTrue()
    {
        Assert.AreEqual(true, RoundTrip(w => w.Bool(true)));
    }

    [TestMethod]
    public void RoundTrip_BoolFalse()
    {
        Assert.AreEqual(false, RoundTrip(w => w.Bool(false)));
    }

    [TestMethod]
    public void RoundTrip_Int()
    {
        Assert.AreEqual(42L, RoundTrip(w => w.Int(42)));
        Assert.AreEqual(long.MinValue, RoundTrip(w => w.Int(long.MinValue)));
        Assert.AreEqual(long.MaxValue, RoundTrip(w => w.Int(long.MaxValue)));
        Assert.AreEqual(0L, RoundTrip(w => w.Int(0)));
    }

    [TestMethod]
    public void RoundTrip_Double()
    {
        Assert.AreEqual(3.14, RoundTrip(w => w.Double(3.14)));
        Assert.AreEqual(double.NaN, RoundTrip(w => w.Double(double.NaN)));
        Assert.AreEqual(double.NegativeInfinity, RoundTrip(w => w.Double(double.NegativeInfinity)));
    }

    [TestMethod]
    public void RoundTrip_String()
    {
        var decoded = (byte[]?)RoundTrip(w => w.String("hello"));
        CollectionAssert.AreEqual("hello"u8.ToArray(), decoded);
    }

    [TestMethod]
    public void RoundTrip_NullString_EncodesAsNil()
    {
        Assert.IsNull(RoundTrip(w => w.String(null)));
    }

    [TestMethod]
    public void RoundTrip_EmptyString()
    {
        var decoded = (byte[]?)RoundTrip(w => w.String(""));
        Assert.AreEqual(0, decoded!.Length);
    }

    [TestMethod]
    public void RoundTrip_Bytes()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x80, 0x01 };
        var decoded = (byte[]?)RoundTrip(w => w.Bytes(payload));
        CollectionAssert.AreEqual(payload, decoded);
    }

    // -------------------------------------------------------------------------------------
    // Round-trip: tables
    // -------------------------------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_EmptyTable()
    {
        var bytes = Encode(w =>
        {
            var mark = w.BeginTable();
            w.EndTable(mark, 0);
        });
        var table = (LuaTable?)Decode(bytes);
        Assert.IsNotNull(table);
        Assert.AreEqual(0, table.Array.Count);
        Assert.AreEqual(0, table.Fields.Count);
    }

    [TestMethod]
    public void RoundTrip_TableWithStringFieldsAndArrayEntries()
    {
        var bytes = Encode(w =>
        {
            var mark = w.BeginTable();
            // array part: key 1 -> "first", key 2 -> "second"
            w.Int(1); w.String("first");
            w.Int(2); w.String("second");
            // hash part: key "name" -> "alice"
            w.String("name"); w.String("alice");
            w.EndTable(mark, 3);
        });

        var table = (LuaTable?)Decode(bytes);
        Assert.IsNotNull(table);
        Assert.AreEqual(2, table.Array.Count);
        CollectionAssert.AreEqual("first"u8.ToArray(), (byte[]?)table[1]);
        CollectionAssert.AreEqual("second"u8.ToArray(), (byte[]?)table[2]);
        CollectionAssert.AreEqual("alice"u8.ToArray(), (byte[]?)table["name"]);
    }

    [TestMethod]
    public void RoundTrip_NestedTable()
    {
        var bytes = Encode(w =>
        {
            var outer = w.BeginTable();
            w.String("inner");
            var inner = w.BeginTable();
            w.String("x"); w.Int(99);
            w.EndTable(inner, 1);
            w.EndTable(outer, 1);
        });

        var table = (LuaTable?)Decode(bytes);
        Assert.IsNotNull(table);
        var nested = (LuaTable?)table["inner"];
        Assert.IsNotNull(nested);
        Assert.AreEqual(99L, nested["x"]);
    }

    [TestMethod]
    public void Writer_Raw_EmptyEncodesNil_AndNonEmptyRoundTrips()
    {
        var empty = Encode(w => w.Raw(ReadOnlySpan<byte>.Empty));
        Assert.IsNull(Decode(empty));

        var inner = Encode(w => w.String("hi"));
        var wrapped = Encode(w => w.Raw(inner));
        CollectionAssert.AreEqual("hi"u8.ToArray(), (byte[]?)Decode(wrapped));
    }

    [TestMethod]
    public void Reader_NestedTablesPastDepthLimit_ThrowsFormatException()
    {
        // Bug class: deep nested tables hang or throw a non-FormatException.
        var w = new LuaWriter();
        var marks = new int[40];
        for (var i = 0; i < marks.Length; i++)
        {
            marks[i] = w.BeginTable();
            w.String("n");
        }

        w.Int(1);
        for (var i = marks.Length - 1; i >= 0; i--)
        {
            w.EndTable(marks[i], 1);
        }

        var ex = Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader(w.ToArray()).Read());
        StringAssert.Contains(ex.Message, "nested too deeply");
    }

    [TestMethod]
    public void RoundTrip_Value_CovarsAllBranchesOfValueMethod()
    {
        var w = new LuaWriter();
        var mark = w.BeginTable();
        w.String("n"); w.Value(null);
        w.String("b_true"); w.Value(true);
        w.String("b_false"); w.Value(false);
        w.String("l"); w.Value(7L);
        w.String("i"); w.Value(3);       // int branch
        w.String("d"); w.Value(1.5);
        w.String("s"); w.Value("hi");
        w.String("bs"); w.Value(new byte[] { 1, 2 });
        w.EndTable(mark, 8);

        var table = (LuaTable?)Decode(w.ToArray());
        Assert.IsNotNull(table);
        Assert.IsNull(table["n"]);
        Assert.AreEqual(true, table["b_true"]);
        Assert.AreEqual(false, table["b_false"]);
        Assert.AreEqual(7L, table["l"]);
        Assert.AreEqual(3L, table["i"]);
        Assert.AreEqual(1.5, table["d"]);
        CollectionAssert.AreEqual("hi"u8.ToArray(), (byte[]?)table["s"]);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[]?)table["bs"]);
    }

    // -------------------------------------------------------------------------------------
    // LuaReader.AsString / AsInt helpers
    // -------------------------------------------------------------------------------------

    [TestMethod]
    public void AsString_ConvertsAllSupportedTypes()
    {
        Assert.IsNull(LuaReader.AsString(null));
        Assert.AreEqual("true", LuaReader.AsString(true));
        Assert.AreEqual("false", LuaReader.AsString(false));
        Assert.AreEqual("42", LuaReader.AsString(42L));
        Assert.AreEqual("3.14", LuaReader.AsString(3.14));
        Assert.AreEqual("hello", LuaReader.AsString("hello"u8.ToArray()));
    }

    [TestMethod]
    public void AsInt_ConvertsNumericTypes()
    {
        Assert.AreEqual(5L, LuaReader.AsInt(5L));
        Assert.AreEqual(5L, LuaReader.AsInt(5.0));  // integral double
        Assert.IsNull(LuaReader.AsInt(5.5));        // non-integral double
        Assert.AreEqual(99L, LuaReader.AsInt("99"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt("not-a-number"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt(null));
    }

    // -------------------------------------------------------------------------------------
    // Error paths
    // -------------------------------------------------------------------------------------

    [TestMethod]
    public void Reader_TruncatedInput_ThrowsFormatException()
    {
        // 'i' tag without 8 bytes of payload.
        Assert.ThrowsExactly<FormatException>(() => Decode([(byte)'i', 0, 0]));
    }

    [TestMethod]
    public void Reader_TruncatedString_ThrowsFormatException()
    {
        // 's' tag with length 10 but only 2 payload bytes.
        var bytes = new byte[] { (byte)'s', 10, 0, 0, 0, (byte)'a', (byte)'b' };
        Assert.ThrowsExactly<FormatException>(() => Decode(bytes));
    }

    [TestMethod]
    public void Reader_UnknownTag_ThrowsFormatException()
    {
        Assert.ThrowsExactly<FormatException>(() => Decode([(byte)'Z']));
        Assert.ThrowsExactly<FormatException>(() => Decode([0x00]));
    }

    [TestMethod]
    public void Reader_EmptyInput_ThrowsFormatException()
    {
        Assert.ThrowsExactly<FormatException>(() => Decode([]));
    }

    [TestMethod]
    public void Reader_StringWithInvalidUtf8_IsReturnedAsRawBytes()
    {
        // A string with invalid UTF-8 (lone 0xFF byte). The reader stores it as byte[]; the caller decides the encoding.
        var payload = new byte[] { 0xFF, 0xFE, 0x00 };
        var w = new LuaWriter();
        w.Bytes(payload);

        var decoded = (byte[]?)Decode(w.ToArray());
        CollectionAssert.AreEqual(payload, decoded, "invalid UTF-8 bytes must survive the codec unchanged");

        // AsString must interpret via UTF-8 replacement (not throw).
        var str = LuaReader.AsString(decoded);
        Assert.IsNotNull(str, "AsString must not return null for a non-null byte[]");
        // The string contains replacement characters for the invalid bytes.
        Assert.IsTrue(str.Contains('\uFFFD') || str.Length > 0, "replacement or best-effort decoding expected");
    }

    [TestMethod]
    public void Reader_NullStringValue_FromWriter_IsNil()
    {
        // LuaWriter.String(null) emits 'n' (nil), not 's' with length 0.
        var bytes = Encode(w => w.String(null));
        Assert.AreEqual((byte)'n', bytes[0], "null string must be encoded as the nil tag");
        Assert.IsNull(Decode(bytes));
    }
}
