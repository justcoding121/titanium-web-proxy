using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Edge-case coverage for <see cref="SessionRawHttp"/>:
/// non-UTF8 binary bodies, compressed bodies written raw, and truncation boundaries.
/// </summary>
[TestClass]
public class SessionRawHttpEdgeTests
{
    // ── non-UTF-8 binary bodies ───────────────────────────────────────────────

    [TestMethod]
    public void Write_NonUtf8_RequestBodyBytes_WrittenRaw()
    {
        // Bytes 0x80–0xBF are invalid as standalone UTF-8 continuation bytes.
        var binaryBody = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0xBF };
        var snap = new SessionSnapshot
        {
            Id = 1,
            Method = "POST",
            Url = "https://ex/bin",
            RequestHeadersText = "Content-Type: application/octet-stream\r\n",
            RequestBodyBytes = binaryBody,
            StatusCode = 204,
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output);

        var bytes = output.ToArray();
        Assert.IsFalse(result.Truncated);
        // The raw binary bytes must appear verbatim in the output stream.
        Assert.IsTrue(bytes.AsSpan().IndexOf(binaryBody) >= 0,
            "Non-UTF8 request body bytes must be written verbatim");
    }

    [TestMethod]
    public void Write_NonUtf8_ResponseBodyBytes_WrittenRaw()
    {
        var binaryBody = new byte[] { 0xEF, 0xBF, 0xBD, 0xFF, 0x00, 0xAA };
        var snap = new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            Url = "https://ex/img.bin",
            ResponseHeadersText = "Content-Type: image/jpeg\r\n",
            ResponseBodyBytes = binaryBody,
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output);

        var bytes = output.ToArray();
        Assert.IsFalse(result.Truncated);
        Assert.IsTrue(bytes.AsSpan().IndexOf(binaryBody) >= 0,
            "Non-UTF8 response body bytes must be written verbatim");
    }

    [TestMethod]
    public void Write_NonUtf8_BodyText_FallsBackToUtf8Encoding()
    {
        // RequestBodyText stores the text representation; Write uses it when bytes are absent.
        var snap = new SessionSnapshot
        {
            Id = 3,
            Method = "POST",
            Url = "https://ex/text",
            RequestBodyText = "Latin-1 special: \u00e9\u00e0\u00fc",
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output);

        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "Latin-1 special:");
        Assert.IsFalse(result.Truncated);
    }

    // ── compressed bodies written as raw bytes ────────────────────────────────

    [TestMethod]
    public void Write_GzipCompressedBody_WritesRawCompressedBytes()
    {
        var plain = Encoding.UTF8.GetBytes("hello gzip world");
        byte[] gzipBody;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                gz.Write(plain);
            }

            gzipBody = ms.ToArray();
        }

        var snap = new SessionSnapshot
        {
            Id = 10,
            Method = "GET",
            Url = "https://ex/gz",
            ResponseHeadersText = "Content-Encoding: gzip\r\n",
            ResponseBodyBytes = gzipBody,
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        SessionRawHttp.Write(snap, output);

        var written = output.ToArray();
        // The gzip magic bytes (1F 8B) must be present in the output.
        Assert.IsTrue(written.AsSpan().IndexOf(new byte[] { 0x1F, 0x8B }) >= 0,
            "Gzip magic bytes must be present in the raw output");
        // Must NOT silently decompress and write 'hello gzip world'.
        var asText = Encoding.UTF8.GetString(written);
        Assert.IsFalse(asText.Contains("hello gzip world", StringComparison.Ordinal),
            "Gzip body must not be transparently decompressed");
    }

    [TestMethod]
    public void Write_BrotliCompressedBody_WritesRawCompressedBytes()
    {
        var plain = Encoding.UTF8.GetBytes("brotli compressed payload");
        byte[] brotliBody;
        using (var ms = new MemoryStream())
        {
            using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                br.Write(plain);
            }

            brotliBody = ms.ToArray();
        }

        var snap = new SessionSnapshot
        {
            Id = 11,
            Method = "GET",
            Url = "https://ex/br",
            ResponseHeadersText = "Content-Encoding: br\r\n",
            ResponseBodyBytes = brotliBody,
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        SessionRawHttp.Write(snap, output);

        var written = output.ToArray();
        // The brotli-compressed bytes must be present verbatim.
        Assert.IsTrue(written.AsSpan().IndexOf(brotliBody) >= 0,
            "Brotli body must be written verbatim without decompression");
    }

    // ── truncation at the byte limit ──────────────────────────────────────────

    [TestMethod]
    public void Write_BodyExactlyAtLimit_NotTruncated()
    {
        var limit = 64L;
        var body = new byte[limit]; // exactly at the limit
        Array.Fill(body, (byte)'A');

        var snap = new SessionSnapshot
        {
            Id = 20,
            Method = "GET",
            Url = "https://ex/exact",
            ResponseBodyBytes = body,
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: limit);

        Assert.IsFalse(result.Truncated, "A body at exactly the limit must not be reported as truncated");
        // All bytes must appear in the output.
        var written = output.ToArray();
        Assert.IsTrue(written.AsSpan().IndexOf(body) >= 0,
            "All body bytes must be present when at exactly the limit");
    }

    [TestMethod]
    public void Write_BodyOneByteOverLimit_IsTruncated()
    {
        var limit = 32L;
        var body = new byte[limit + 1]; // one byte over
        Array.Fill(body, (byte)'B');

        var snap = new SessionSnapshot
        {
            Id = 21,
            Method = "GET",
            Url = "https://ex/over",
            ResponseBodyBytes = body,
            StatusCode = 200,
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: limit);

        Assert.IsTrue(result.Truncated, "A body one byte over the limit must be truncated");
        // Only 'limit' bytes of the body should appear.
        var written = output.ToArray();
        var bodyStart = FindSequence(written, (byte)'B');
        Assert.IsTrue(bodyStart >= 0, "Body content must start with fill bytes");
        // Count the fill bytes actually written.
        var fillCount = 0;
        for (var i = bodyStart; i < written.Length && written[i] == (byte)'B'; i++)
        {
            fillCount++;
        }

        Assert.AreEqual((int)limit, fillCount,
            $"Exactly {limit} body bytes should be written; got {fillCount}");
    }

    [TestMethod]
    public void Write_NoBodiesAndNoStatus_WritesOnlyRequestLine()
    {
        var snap = new SessionSnapshot
        {
            Id = 30,
            Method = "OPTIONS",
            Url = "https://ex/health",
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output);

        var text = Encoding.ASCII.GetString(output.ToArray());
        StringAssert.StartsWith(text, "OPTIONS /health HTTP/1.1\r\n");
        Assert.IsFalse(result.Truncated);
        // No response block when StatusCode is null; the only HTTP/1.1 token is at the end of the request line.
        // A response status line would look like "\r\nHTTP/1.1 NNN".
        Assert.IsFalse(text.Contains("\r\nHTTP/1.1 ", StringComparison.Ordinal),
            "No response status line should be present when StatusCode is null");
    }

    [TestMethod]
    public void Write_RequestWithOnlyTextBody_UsesTextFallback()
    {
        var snap = new SessionSnapshot
        {
            Id = 31,
            Method = "PATCH",
            Url = "https://ex/text",
            RequestBodyText = "patch-text-body",
            // RequestBodyBytes intentionally absent
        };

        using var output = new MemoryStream();
        var result = SessionRawHttp.Write(snap, output);

        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "patch-text-body",
            "RequestBodyText must be used when RequestBodyBytes is null");
        Assert.IsFalse(result.Truncated);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int FindSequence(byte[] haystack, byte value)
    {
        for (var i = 0; i < haystack.Length; i++)
        {
            if (haystack[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
