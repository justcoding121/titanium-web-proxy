using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Waf;

namespace Titanium.Plus.Tests;

/// <summary>Direct unit tests for <see cref="WafText"/> encoding helpers.</summary>
[TestClass]
public class WafTextTests
{
    // ---------------------------------------------------------------- UrlDecode

    private static string UrlDecode(string input, bool plusAsSpace = true)
    {
        var dst = new char[input.Length];
        var len = WafText.UrlDecode(input.AsSpan(), dst.AsSpan(), plusAsSpace);
        return new string(dst, 0, len);
    }

    [TestMethod]
    [DataRow("%41%42%43", "ABC")]
    [DataRow("%61%62%63", "abc")]
    [DataRow("%20", " ")]
    [DataRow("%00", "\0")]
    [DataRow("hello%20world", "hello world")]
    public void UrlDecode_BasicPercentHex(string input, string expected) =>
        Assert.AreEqual(expected, UrlDecode(input));

    [TestMethod]
    public void UrlDecode_PlusIsSpaceWhenEnabled()
    {
        Assert.AreEqual("hello world", UrlDecode("hello+world", plusAsSpace: true));
        // plus is literal when disabled
        Assert.AreEqual("hello+world", UrlDecode("hello+world", plusAsSpace: false));
    }

    [TestMethod]
    public void UrlDecode_UnicodeEscape_PercentU()
    {
        Assert.AreEqual("A", UrlDecode("%u0041"));
        Assert.AreEqual("<", UrlDecode("%u003c"));
        Assert.AreEqual("<", UrlDecode("%U003C"));
    }

    [TestMethod]
    public void UrlDecode_TwoByteutf8_YieldsOneChar()
    {
        // %c3%a9 = é (U+00E9)
        Assert.AreEqual("\u00e9", UrlDecode("%c3%a9"));
    }

    [TestMethod]
    public void UrlDecode_OverlongUtf8_SequenceIsDecoded()
    {
        // %c0%ae is an overlong encoding of '.' (U+002E). Old servers accepted this; the WAF normalises it.
        var result = UrlDecode("%c0%ae");
        Assert.AreEqual(1, result.Length, "overlong form collapses to one char");
        // The decoded codepoint is (0xc0 & 0x1F) << 6 | (0xae & 0x3F) = 0x2E = '.'
        Assert.AreEqual((char)0x2E, result[0]);
    }

    [TestMethod]
    public void UrlDecode_InvalidEscapeStaysAsIs()
    {
        Assert.AreEqual("%", UrlDecode("%"));
        Assert.AreEqual("%g0", UrlDecode("%g0"));
        Assert.AreEqual("%4", UrlDecode("%4"));
        Assert.AreEqual("%zz", UrlDecode("%zz"));
    }

    [TestMethod]
    public void UrlDecode_EmptyInput_ReturnsEmpty() =>
        Assert.AreEqual(string.Empty, UrlDecode(string.Empty));

    [TestMethod]
    public void UrlDecode_NoEscapes_ReturnsSameChars() =>
        Assert.AreEqual("hello world!", UrlDecode("hello world!"));

    [TestMethod]
    public void UrlDecode_AdjacentEscapes_EachDecodedIndependently()
    {
        Assert.AreEqual("AB", UrlDecode("%41%42"));
        Assert.AreEqual(" ?", UrlDecode("%20%3F"));
    }

    // ---------------------------------------------------------------- HasPercentEscape

    [TestMethod]
    [DataRow("%41", true)]
    [DataRow("%u0041", true)]
    [DataRow("hello", false)]
    [DataRow("", false)]
    [DataRow("%", false)]
    [DataRow("%g0", false)]
    [DataRow("a%20b", true)]
    public void HasPercentEscape(string input, bool expected) =>
        Assert.AreEqual(expected, WafText.HasPercentEscape(input.AsSpan()));

    // ---------------------------------------------------------------- RemoveNulls

    private static string RemoveNulls(string input)
    {
        var dst = new char[input.Length];
        var len = WafText.RemoveNulls(input.AsSpan(), dst.AsSpan());
        return new string(dst, 0, len);
    }

    [TestMethod]
    public void RemoveNulls_RemovesAllNulChars()
    {
        Assert.AreEqual("abc", RemoveNulls("a\0b\0c"));
        Assert.AreEqual("", RemoveNulls("\0\0\0"));
        Assert.AreEqual("abc", RemoveNulls("abc"));
    }

    [TestMethod]
    public void RemoveNulls_EmptyInput_ReturnsEmpty() =>
        Assert.AreEqual(string.Empty, RemoveNulls(string.Empty));

    // ---------------------------------------------------------------- Lowercase

    private static string Lowercase(string input)
    {
        var dst = new char[input.Length];
        var len = WafText.Lowercase(input.AsSpan(), dst.AsSpan());
        return new string(dst, 0, len);
    }

    [TestMethod]
    public void Lowercase_LowercasesAsciiLetters()
    {
        Assert.AreEqual("hello world", Lowercase("Hello World"));
        Assert.AreEqual("abc", Lowercase("ABC"));
        Assert.AreEqual("abc123", Lowercase("ABC123"));
        Assert.AreEqual("", Lowercase(""));
    }

    [TestMethod]
    public void Lowercase_PreservesNonAscii()
    {
        // Only ASCII letters are tested; the method uses char.ToLowerInvariant.
        Assert.AreEqual("ä", Lowercase("Ä"));
    }

    // ---------------------------------------------------------------- HtmlEntityDecode

    private static string HtmlEntityDecode(string input)
    {
        var dst = new char[input.Length * 2]; // generous buffer
        var len = WafText.HtmlEntityDecode(input.AsSpan(), dst.AsSpan());
        return new string(dst, 0, len);
    }

    [TestMethod]
    [DataRow("&#60;", "<")]
    [DataRow("&#x3c;", "<")]
    [DataRow("&#X3C;", "<")]
    [DataRow("&#62;", ">")]
    [DataRow("&#65;", "A")]
    public void HtmlEntityDecode_NumericEntities(string input, string expected) =>
        Assert.AreEqual(expected, HtmlEntityDecode(input));

    [TestMethod]
    [DataRow("&lt;", "<")]
    [DataRow("&gt;", ">")]
    [DataRow("&amp;", "&")]
    [DataRow("&quot;", "\"")]
    [DataRow("&apos;", "'")]
    [DataRow("&colon;", ":")]
    [DataRow("&sol;", "/")]
    [DataRow("&bsol;", "\\")]
    [DataRow("&nbsp;", " ")]
    public void HtmlEntityDecode_NamedEntities_WithSemicolon(string input, string expected) =>
        Assert.AreEqual(expected, HtmlEntityDecode(input));

    [TestMethod]
    public void HtmlEntityDecode_ShortNamedEntities_WorkWithoutSemicolon()
    {
        // lt, gt, amp, quot are the legacy entities browsers accept without a semicolon.
        Assert.AreEqual("<", HtmlEntityDecode("&lt"));
        Assert.AreEqual(">", HtmlEntityDecode("&gt"));
        Assert.AreEqual("&", HtmlEntityDecode("&amp"));
        Assert.AreEqual("\"", HtmlEntityDecode("&quot"));
    }

    [TestMethod]
    public void HtmlEntityDecode_LongNamedEntityWithoutSemicolon_StaysAsIs()
    {
        // Entities longer than 4 chars must have a semicolon (e.g. 'colon' = 5 chars).
        Assert.AreEqual("&colon", HtmlEntityDecode("&colon"));
        Assert.AreEqual("&equals", HtmlEntityDecode("&equals"));
    }

    [TestMethod]
    public void HtmlEntityDecode_FourCharNamedEntity_DecodesEvenWithoutSemicolon()
    {
        // Entities up to 4 chars decode without a semicolon (&nbsp; = 4 chars decodes to space).
        Assert.AreEqual(" ", HtmlEntityDecode("&nbsp"));
    }

    [TestMethod]
    public void HtmlEntityDecode_InvalidOrEmptyEntity_StaysAsIs()
    {
        Assert.AreEqual("&", HtmlEntityDecode("&"));
        Assert.AreEqual("&;", HtmlEntityDecode("&;"));
        Assert.AreEqual("&unknown;", HtmlEntityDecode("&unknown;"));
        Assert.AreEqual("&#;", HtmlEntityDecode("&#;"));
        Assert.AreEqual("&#0;", HtmlEntityDecode("&#0;"));       // 0 not valid
        Assert.AreEqual("&#xFFFF0;", HtmlEntityDecode("&#xFFFF0;")); // > 0xFFFF not valid
    }

    [TestMethod]
    public void HtmlEntityDecode_NumericEntityWithoutSemicolon()
    {
        // The semicolon is optional for numeric entities.
        Assert.AreEqual("<", HtmlEntityDecode("&#60"));
        Assert.AreEqual("<", HtmlEntityDecode("&#x3c"));
    }

    [TestMethod]
    public void HtmlEntityDecode_MixedContent_OnlyEntitiesAreDecoded()
    {
        Assert.AreEqual("a<b>c", HtmlEntityDecode("a&lt;b&gt;c"));
        Assert.AreEqual("a&unknown;b", HtmlEntityDecode("a&unknown;b"));
    }

    // ---------------------------------------------------------------- Apply dispatch

    [TestMethod]
    public void Apply_UrlDecode_DispatchesToUrlDecode()
    {
        var dst = new char[10];
        var len = WafText.Apply(WafTransform.UrlDecode, "%41".AsSpan(), dst.AsSpan());
        Assert.AreEqual("A", new string(dst, 0, len));
    }

    [TestMethod]
    public void Apply_Lowercase_DispatchesToLowercase()
    {
        var dst = new char[10];
        var len = WafText.Apply(WafTransform.Lowercase, "ABC".AsSpan(), dst.AsSpan());
        Assert.AreEqual("abc", new string(dst, 0, len));
    }
}
