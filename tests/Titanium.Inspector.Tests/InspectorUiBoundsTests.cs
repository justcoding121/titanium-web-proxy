using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Bounds that keep the Inspect panel responsive: everything handed to a TextBox is capped and
///     renderable, decompression on the UI thread is bounded, and the session diff stays linear.
/// </summary>
[TestClass]
public class InspectorUiBoundsTests
{
    [TestMethod]
    public void ForTextBox_ShortRenderableText_ReturnsSameInstance()
    {
        var text = "=== Request ===\n(empty)\n\n=== Response ===\n{\"a\":1}\tök";
        Assert.AreSame(text, InspectorDisplayText.ForTextBox(text));
        Assert.AreEqual("", InspectorDisplayText.ForTextBox(null));
        Assert.AreEqual("", InspectorDisplayText.ForTextBox(""));
    }

    [TestMethod]
    public void ForTextBox_ScrubsControlAndReplacementCharacters()
    {
        var text = "ok\uFFFD\u0000\u0001\u007F\u0085end\r\n\t";
        Assert.AreEqual("ok.....end\r\n\t", InspectorDisplayText.ForTextBox(text));
    }

    [TestMethod]
    public void ForTextBox_LargeText_IsCappedWithNote()
    {
        var text = new string('a', InspectorDisplayText.MaxTextChars + 5000);
        var shown = InspectorDisplayText.ForTextBox(text);

        Assert.IsTrue(shown.StartsWith(new string('a', InspectorDisplayText.MaxTextChars), StringComparison.Ordinal));
        StringAssert.Contains(shown, "display limited");
        Assert.IsTrue(shown.Length < InspectorDisplayText.MaxTextChars + 400);
        Assert.IsFalse(shown.Contains("binary content", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ForTextBox_BinaryText_UsesTighterCapAndNoReplacementCharacters()
    {
        var wire = new byte[InspectorBodyLimits.MaxBodyBytes];
        new Random(3).NextBytes(wire);
        var text = InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(wire));
        Assert.IsTrue(InspectorDisplayText.LooksBinary(text));

        var shown = InspectorDisplayText.ForTextBox(text);

        Assert.IsTrue(shown.Length < InspectorDisplayText.MaxBinaryChars + 400, $"len={shown.Length}");
        Assert.IsFalse(shown.Contains('\uFFFD'));
        StringAssert.Contains(shown, "binary content");
    }

    [TestMethod]
    public void ForTextBox_FewReplacementCharsInLargeText_AreNotTreatedAsBinary()
    {
        var text = new string('x', 50_000) + "\uFFFD" + new string('y', 50_000);
        Assert.IsFalse(InspectorDisplayText.LooksBinary(text));

        var shown = InspectorDisplayText.ForTextBox(text);

        Assert.AreEqual(100_001, shown.Length);
        Assert.AreEqual('.', shown[50_000]);
    }

    [TestMethod]
    public void ForTextBox_DoesNotSplitSurrogatePairAtCap()
    {
        var text = new string('a', InspectorDisplayText.MaxTextChars - 1) + "😀" + "tail";
        var shown = InspectorDisplayText.ForTextBox(text);

        var head = shown[..(InspectorDisplayText.MaxTextChars - 1)];
        Assert.AreEqual(new string('a', InspectorDisplayText.MaxTextChars - 1), head);
        Assert.IsTrue(shown[InspectorDisplayText.MaxTextChars - 1] is '\r' or '\n');
        Assert.IsFalse(shown.Any(char.IsSurrogate));
    }

    [TestMethod]
    public void TryDecompress_MaxOutput_StopsDecompressionBomb()
    {
        var raw = new byte[8 * 1024 * 1024]; // compresses to a few KB
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(raw);
        }

        var bomb = packed.ToArray();
        Assert.IsTrue(bomb.Length < 64 * 1024);

        var bounded = SessionInspectors.TryDecompress(bomb, "gzip", 1000);

        Assert.IsNotNull(bounded);
        Assert.AreEqual(1000, bounded.Length);
        Assert.AreEqual(raw.Length, SessionInspectors.TryDecompress(bomb, "gzip")!.Length);
    }

    [TestMethod]
    public void FormatLabeledHex_CompressedBody_DecodesOnlyHexWindowAndKeepsEllipsis()
    {
        var raw = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(raw);
        }

        var hex = SessionInspectors.FormatLabeledHex(
            "", "Content-Encoding: gzip\r\n", null, packed.ToArray());

        StringAssert.EndsWith(hex, " …");
        StringAssert.Contains(hex, "00 01 02 03");
        Assert.IsTrue(hex.Length < InspectorBodyLimits.MaxHexBytes * 3 + 200);
    }

    [TestMethod]
    public void SessionDiff_LargeDissimilarBodies_StaysLinearAndKeepsLookaheadSemantics()
    {
        var left = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            Url = "http://a.test/",
            ResponseBodyText = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => "L" + i)),
        };
        var right = new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            Url = "http://a.test/",
            ResponseBodyText = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => "R" + i)),
        };

        var sw = Stopwatch.StartNew();
        var diff = SessionDiff.Compare(left, right);
        sw.Stop();

        Assert.IsTrue(diff.HasDifferences);
        Assert.IsTrue(sw.ElapsedMilliseconds < 3000, $"diff took {sw.ElapsedMilliseconds} ms");

        // A match within 8 lines is still reported as an insert, not a replace.
        var near = SessionDiff.Compare(
            new SessionSnapshot { Id = 3, Method = "GET", Url = "u", ResponseBodyText = "a\nb\nc\nd" },
            new SessionSnapshot { Id = 4, Method = "GET", Url = "u", ResponseBodyText = "a\nX\nb\nc\nd" });
        StringAssert.Contains(near.Text, "+ X");
        Assert.IsFalse(near.Text.Contains("- b", StringComparison.Ordinal));
    }
}
