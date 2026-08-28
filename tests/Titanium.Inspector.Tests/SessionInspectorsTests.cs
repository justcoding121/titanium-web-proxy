using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionInspectorsTests
{
    [TestMethod]
    public void ParseCookies_FromCookieAndSetCookie()
    {
        var fromCookie = SessionInspectors.ParseCookies(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "a=1; b=two; flag; =empty",
            });
        Assert.AreEqual("1", fromCookie["a"]);
        Assert.AreEqual("two", fromCookie["b"]);
        Assert.IsFalse(fromCookie.ContainsKey("flag"));

        var fromSet = SessionInspectors.ParseCookies(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Set-Cookie"] = "sid=abc; Path=/",
            });
        Assert.AreEqual("abc", fromSet["sid"]);
        Assert.AreEqual("/", fromSet["Path"]);

        Assert.AreEqual(0, SessionInspectors.ParseCookies(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = "example.com",
            }).Count);
    }

    [TestMethod]
    public void ParseQuery_PairsFlagsAndEmpty()
    {
        Assert.AreEqual(0, SessionInspectors.ParseQuery("https://example/").Count);
        Assert.AreEqual(0, SessionInspectors.ParseQuery("https://example/?").Count);

        var q = SessionInspectors.ParseQuery("https://example/?a=1&flag&b=hello%20world");
        Assert.AreEqual("1", q["a"]);
        Assert.AreEqual("", q["flag"]);
        Assert.AreEqual("hello world", q["b"]);
    }

    [TestMethod]
    public void ToHex_TruncatesWithEllipsis()
    {
        Assert.AreEqual("", SessionInspectors.ToHex(null));
        Assert.AreEqual("", SessionInspectors.ToHex([]));
        Assert.AreEqual("01 02 FF", SessionInspectors.ToHex([0x01, 0x02, 0xFF]));

        var bytes = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
        var truncated = SessionInspectors.ToHex(bytes, maxBytes: 3);
        Assert.AreEqual("00 01 02 …", truncated);
    }

    [TestMethod]
    public void TryFormatJson_IndentsOrReturnsOriginal()
    {
        Assert.AreEqual("", SessionInspectors.TryFormatJson(null));
        Assert.AreEqual("   ", SessionInspectors.TryFormatJson("   "));
        Assert.AreEqual("not-json", SessionInspectors.TryFormatJson("not-json"));

        var formatted = SessionInspectors.TryFormatJson("""{"a":1}""");
        StringAssert.Contains(formatted, "\n");
        StringAssert.Contains(formatted, "\"a\"");
    }

    [TestMethod]
    public void TryDecompress_GzipDeflateBrotliIdentityAndCorrupt()
    {
        Assert.IsNull(SessionInspectors.TryDecompress(null, "gzip"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), SessionInspectors.TryDecompress([], "gzip")!);
        var raw = "hello"u8.ToArray();
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(raw, null)!);
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(raw, "")!);
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(raw, "identity")!);

        var gzip = Compress(raw, (ms, mode) => new GZipStream(ms, mode));
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(gzip, "gzip")!);

        var deflate = Compress(raw, (ms, mode) => new DeflateStream(ms, mode));
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(deflate, "deflate")!);

        var brotli = Compress(raw, (ms, mode) => new BrotliStream(ms, mode));
        CollectionAssert.AreEqual(raw, SessionInspectors.TryDecompress(brotli, "br")!);

        var corrupt = new byte[] { 0x1F, 0x8B, 0x00, 0x01 };
        CollectionAssert.AreEqual(corrupt, SessionInspectors.TryDecompress(corrupt, "gzip")!);
    }

    private static byte[] Compress(byte[] input, Func<MemoryStream, CompressionMode, Stream> factory)
    {
        using var ms = new MemoryStream();
        using (var codec = factory(ms, CompressionMode.Compress))
        {
            codec.Write(input, 0, input.Length);
        }

        return ms.ToArray();
    }
}
