using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Streaming extraction of one body string out of a spilled HAR (raw-view copy path): escapes must decode to the
///     original bytes, caps must truncate and say so, and malformed input must never throw into the UI.
/// </summary>
[TestClass]
public class HarBodyCopyBehaviorTests
{
    private static MemoryStream Har(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Content(string contentObjectBody) =>
        "{\"log\":{\"entries\":[{\"response\":{\"content\":{" + contentObjectBody + "}}}]}}";

    private static (bool Truncated, byte[] Bytes) CopyContent(string json, long maxBytes = 1024)
    {
        using var har = Har(json);
        using var output = new MemoryStream();
        var truncated = HarBodyCopy.CopyContent(har, output, maxBytes);
        return (truncated, output.ToArray());
    }

    // Bug class: JSON escapes are copied literally, so the raw view shows backslash sequences instead of the body.
    [TestMethod]
    public void CopyContent_DecodesEveryJsonEscape()
    {
        var (truncated, bytes) = CopyContent(
            Content("\"mimeType\":\"text/plain\",\"text\":\"a\\\"b\\\\c\\/d\\n\\t\\r\\b\\f\\u0041\""));

        Assert.IsFalse(truncated);
        Assert.AreEqual("a\"b\\c/d\n\t\r\b\fA", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: a malformed \u escape consumes the closing quote and swallows the rest of the document.
    [TestMethod]
    public void CopyContent_MalformedUnicodeEscape_YieldsPlaceholderAndKeepsScanning()
    {
        var (_, bytes) = CopyContent(Content("\"text\":\"x\\uZZZZy\",\"size\":3"));

        Assert.AreEqual("x?y", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: the size cap is applied to the encoded JSON text instead of the decoded body bytes.
    [TestMethod]
    public void CopyContent_CapsDecodedBytes_AndReportsTruncation()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"ab\\ncdef\""), maxBytes: 4);

        Assert.IsTrue(truncated, "dropping bytes must be reported");
        Assert.AreEqual("ab\nc", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: a body exactly at the cap is reported as truncated.
    [TestMethod]
    public void CopyContent_BodyExactlyAtTheCap_IsNotTruncated()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"abcd\""), maxBytes: 4);

        Assert.IsFalse(truncated);
        Assert.AreEqual("abcd", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: base64 bodies are shown as base64 text (binary payloads unreadable) or decode past the cap.
    [TestMethod]
    public void CopyContent_Base64Body_IsDecodedToBytes()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"aGVsbG8gd29ybGQh\",\"encoding\":\"base64\""));

        Assert.IsFalse(truncated);
        Assert.AreEqual("hello world!", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: the encoding marker is honored only when it precedes the text, so reordered HARs show base64 raw.
    [TestMethod]
    public void CopyContent_Base64EncodingDeclaredBeforeText_IsStillHonored()
    {
        var (_, bytes) = CopyContent(Content("\"encoding\":\"BASE64\",\"text\":\"aGk=\""));

        Assert.AreEqual("hi", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: decoded base64 beyond the cap is written in full.
    [TestMethod]
    public void CopyContent_Base64BodyOverTheCap_IsCutAndReported()
    {
        var (truncated, bytes) = CopyContent(
            Content("\"text\":\"aGVsbG8gd29ybGQh\",\"encoding\":\"base64\""), maxBytes: 5);

        Assert.IsTrue(truncated);
        Assert.IsTrue(bytes.Length is > 0 and <= 5, "never more than the cap, never nothing: " + bytes.Length);
        StringAssert.StartsWith("hello world!", Encoding.UTF8.GetString(bytes), "the copy must be a prefix of the body");
    }

    // Bug class: base64 cut mid-quad (earlier truncation) throws or silently loses the tail.
    [TestMethod]
    public void CopyContent_Base64WithPartialTrailingQuad_DecodesWholeQuadsAndFlagsTruncation()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"aGVsbG8gd2\",\"encoding\":\"base64\""));

        Assert.IsTrue(truncated);
        Assert.AreEqual("hello ", Encoding.UTF8.GetString(bytes.Take(6).ToArray()));
    }

    // Bug class: text shorter than one base64 quad is dropped without telling the user.
    [TestMethod]
    public void CopyContent_Base64ShorterThanOneQuad_WritesNothingButFlagsTruncation()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"abc\",\"encoding\":\"base64\""));

        Assert.IsTrue(truncated);
        Assert.AreEqual(0, bytes.Length);
    }

    // Bug class: invalid base64 throws FormatException into the caller instead of falling back to the text.
    [TestMethod]
    public void CopyContent_InvalidBase64_FallsBackToTheLiteralText()
    {
        var (truncated, bytes) = CopyContent(Content("\"text\":\"!!!!\",\"encoding\":\"base64\""));

        Assert.IsTrue(truncated);
        Assert.AreEqual("!!!!", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: a HAR cut off mid-document (crash while spilling) throws into the raw view.
    [TestMethod]
    [DataRow("")]
    [DataRow("{\"log\":{\"entries\":[{\"response\":{\"content\":{\"text\":\"abc")]
    [DataRow("{\"content\":")]
    [DataRow("[1,2")]
    public void CopyContent_TruncatedDocument_DoesNotThrow(string json)
    {
        var (_, bytes) = CopyContent(json);

        Assert.IsTrue(bytes.Length <= 3);
    }

    // Bug class: documents without any content text report phantom truncation or write bytes.
    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("\"just a string\"")]
    [DataRow("42")]
    [DataRow("{\"content\":{\"size\":0,\"compression\":-1,\"ok\":true,\"none\":null,\"list\":[1,\"a\",[],{}]}}")]
    public void CopyContent_DocumentWithoutText_WritesNothingAndIsNotTruncated(string json)
    {
        var (truncated, bytes) = CopyContent(json);

        Assert.IsFalse(truncated);
        Assert.AreEqual(0, bytes.Length);
    }

    // Bug class: a text property outside any "content" object (e.g. a header value named text) leaks into the body.
    [TestMethod]
    public void CopyContent_IgnoresTextOutsideContent()
    {
        var (_, bytes) = CopyContent("{\"request\":{\"text\":\"not-a-body\"},\"response\":{\"content\":{\"text\":\"real\"}}}");

        Assert.AreEqual("real", Encoding.UTF8.GetString(bytes));
    }

    // Bug class: request body extraction reads response content or the wrong parent.
    [TestMethod]
    public void CopyProperty_ReadsPostDataText_AndIgnoresResponseContent()
    {
        using var har = Har(
            "{\"log\":{\"entries\":[{\"request\":{\"postData\":{\"mimeType\":\"application/json\",\"text\":\"{\\\"a\\\":1}\"}},"
            + "\"response\":{\"content\":{\"text\":\"resp\"}}}]}}");
        using var output = new MemoryStream();

        var truncated = HarBodyCopy.CopyProperty(har, "postData", "text", output, 1024, encoding: null);

        Assert.IsFalse(truncated);
        Assert.AreEqual("{\"a\":1}", Encoding.UTF8.GetString(output.ToArray()));
    }

    // Bug class: a caller-supplied base64 encoding is ignored for request bodies.
    [TestMethod]
    public void CopyProperty_WithBase64Encoding_DecodesTheValue()
    {
        using var har = Har("{\"request\":{\"postData\":{\"text\":\"AAEC\"}}}");
        using var output = new MemoryStream();

        var truncated = HarBodyCopy.CopyProperty(har, "postData", "text", output, 1024, encoding: "base64");

        Assert.IsFalse(truncated);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2 }, output.ToArray());
    }

    // Bug class: reading starts at the stream's current position, so a second extraction from the same stream is empty.
    [TestMethod]
    public void CopyContent_RewindsTheSourceStream_ForRepeatedReads()
    {
        using var har = Har(Content("\"text\":\"again\""));
        har.Position = har.Length;
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        HarBodyCopy.CopyContent(har, first, 64);
        HarBodyCopy.CopyContent(har, second, 64);

        Assert.AreEqual("again", Encoding.UTF8.GetString(first.ToArray()));
        Assert.AreEqual("again", Encoding.UTF8.GetString(second.ToArray()));
    }

    // Bug class: non-ASCII body text written by the spill writer comes back as '?' in the raw view.
    [TestMethod]
    public void SpilledHar_WithNonAsciiBody_RoundTripsToUtf8Bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-har-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cache = new SessionBodyDiskCache(root, 0, TimeSpan.FromDays(1));
            const string text = "h\u00e9llo \u20ac \u4e2d\u6587";
            cache.Write(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                StatusCode = 200,
                ResponseBodyBytes = Encoding.UTF8.GetBytes(text),
                ResponseBodyText = text,
                ResponseBodyCapture = BodyCaptureState.Complete,
            });

            Assert.IsTrue(cache.TryOpenRead(1, out var stream));
            using (stream)
            using (var output = new MemoryStream())
            {
                HarBodyCopy.CopyContent(stream!, output, 4096);
                Assert.AreEqual(text, Encoding.UTF8.GetString(output.ToArray()));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
