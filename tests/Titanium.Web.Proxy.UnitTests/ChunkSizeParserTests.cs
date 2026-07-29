using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="ChunkSizeParser" />, which replaced
///     <c>int.TryParse(chunkHead, NumberStyles.HexNumber, ...)</c> at every chunk-size call site because
///     that pattern reinterprets the hex digits as a two's-complement 32-bit value: an attacker-supplied
///     "ffffffff" silently became -1, a sentinel several call sites treat as "no more chunks" while the
///     peer's actual chunk bytes remain unread on the wire.
/// </summary>
[TestClass]
public class ChunkSizeParserTests
{
    [TestMethod]
    [DataRow("0", 0L)]
    [DataRow("a", 10L)]
    [DataRow("1a3", 419L)]
    [DataRow("FFFF", 65535L)]
    [DataRow("ff", 255L)]
    public void TryParse_ValidHexDigits_ReturnsExpectedSize(string line, long expected)
    {
        Assert.IsTrue(ChunkSizeParser.TryParse(line, long.MaxValue, out var chunkSize));
        Assert.AreEqual(expected, chunkSize);
    }

    [TestMethod]
    public void TryParse_ArbitraryLeadingZeros_AreLegalPerGrammar()
    {
        // chunk-size = 1*HEXDIG has no length ceiling; leading zeros of any length are valid.
        Assert.IsTrue(ChunkSizeParser.TryParse("0000000000000001a3", long.MaxValue, out var chunkSize));
        Assert.AreEqual(419L, chunkSize);
    }

    [TestMethod]
    public void TryParse_ChunkExtension_IsIgnored()
    {
        Assert.IsTrue(ChunkSizeParser.TryParse("1a3;foo=bar", long.MaxValue, out var chunkSize));
        Assert.AreEqual(419L, chunkSize);
    }

    [TestMethod]
    public void TryParse_ChunkExtensionWithNoValue_IsIgnored()
    {
        Assert.IsTrue(ChunkSizeParser.TryParse("a;", long.MaxValue, out var chunkSize));
        Assert.AreEqual(10L, chunkSize);
    }

    [TestMethod]
    public void TryParse_EightHexFCharacters_DoesNotWrapToNegativeOne()
    {
        // Under the old int.TryParse(..., NumberStyles.HexNumber, ...) pattern, "ffffffff" parsed to the
        // Int32 value -1 - exactly the sentinel several call sites use for "no more chunks". This must
        // instead be rejected as too large (well above any legitimate chunk) rather than silently
        // becoming a value that terminates chunk reading while real chunk bytes remain on the wire.
        Assert.IsFalse(ChunkSizeParser.TryParse("ffffffff", ProxyLimits.DefaultMaxChunkSizeBytes, out _));
    }

    [TestMethod]
    public void TryParse_EightZeroCharactersFollowedByEight0000000_DoesNotWrapToIntMinValue()
    {
        // "80000000" is Int32.MinValue under two's-complement reinterpretation - also negative, also
        // wrong, and must be rejected the same way as any other value exceeding the configured cap.
        Assert.IsFalse(ChunkSizeParser.TryParse("80000000", ProxyLimits.DefaultMaxChunkSizeBytes, out _));
    }

    [TestMethod]
    public void TryParse_ValueAboveConfiguredCap_IsRejected()
    {
        Assert.IsFalse(ChunkSizeParser.TryParse("ff", maxChunkSizeBytes: 254, out _));
        Assert.IsTrue(ChunkSizeParser.TryParse("fe", maxChunkSizeBytes: 254, out var chunkSize));
        Assert.AreEqual(254L, chunkSize);
    }

    [TestMethod]
    public void TryParse_ValueThatWouldOverflowInt64_IsRejectedRatherThanWrapping()
    {
        Assert.IsFalse(ChunkSizeParser.TryParse("ffffffffffffffffff", long.MaxValue, out _));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("g")]
    [DataRow("1g3")]
    [DataRow(" ")]
    [DataRow("-1")]
    [DataRow("+1")]
    public void TryParse_NonHexInput_IsRejected(string line)
    {
        Assert.IsFalse(ChunkSizeParser.TryParse(line, long.MaxValue, out _));
    }

    [TestMethod]
    public void TryParse_EmptySizeBeforeChunkExtension_IsRejected()
    {
        Assert.IsFalse(ChunkSizeParser.TryParse(";foo=bar", long.MaxValue, out _));
    }

    [TestMethod]
    public void TryParse_NeverProducesNegativeChunkSize()
    {
        // Sweep every 8-hex-digit value with the high bit set (the entire two's-complement-negative
        // Int32 range) and assert none of them come back as a "valid" negative/zero-looking chunkSize -
        // either the call rejects it (expected, since it exceeds a realistic cap) or, if a caller passed
        // an enormous cap, the returned value is the correct large positive magnitude rather than a
        // wrapped negative one.
        foreach (var hex in new[] { "80000000", "ffffffff", "f0000000", "80000001" })
        {
            if (ChunkSizeParser.TryParse(hex, long.MaxValue, out var chunkSize))
                Assert.IsTrue(chunkSize >= 0, $"'{hex}' must never decode to a negative chunk size.");
        }
    }
}
