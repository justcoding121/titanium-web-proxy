using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Plus.Tests;

[TestClass]
public class GrpcStreamingCompressionTests
{
    [TestMethod]
    public void GrpcFrames_RoundTrip_GzipFlag()
    {
        var payload = Encoding.UTF8.GetBytes("abc");
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(payload);
        }

        var compressed = ms.ToArray();
        var framed = EncodeFrame(compressed, compressed: true);
        Assert.AreEqual(1, framed[0]);
        Assert.IsTrue(TryReadFrame(framed, out var flag, out var body));
        Assert.IsTrue(flag);
        Assert.AreEqual(compressed.Length, body.Length);
    }

    [TestMethod]
    public void MultiFrame_Concat_Readable()
    {
        var a = EncodeFrame("one"u8, false);
        var b = EncodeFrame("two"u8, false);
        var all = a.Concat(b).ToArray();
        Assert.IsTrue(TryReadFrame(all, out _, out var p1));
        Assert.AreEqual("one", Encoding.UTF8.GetString(p1));
        Assert.IsTrue(TryReadFrame(all.AsSpan(a.Length), out _, out var p2));
        Assert.AreEqual("two", Encoding.UTF8.GetString(p2));
    }

    private static byte[] EncodeFrame(ReadOnlySpan<byte> payload, bool compressed)
    {
        var result = new byte[5 + payload.Length];
        result[0] = compressed ? (byte)1 : (byte)0;
        result[1] = (byte)((payload.Length >> 24) & 0xff);
        result[2] = (byte)((payload.Length >> 16) & 0xff);
        result[3] = (byte)((payload.Length >> 8) & 0xff);
        result[4] = (byte)(payload.Length & 0xff);
        payload.CopyTo(result.AsSpan(5));
        return result;
    }

    private static bool TryReadFrame(ReadOnlySpan<byte> buffer, out bool compressed, out ReadOnlySpan<byte> payload)
    {
        compressed = false;
        payload = default;
        if (buffer.Length < 5)
        {
            return false;
        }

        compressed = (buffer[0] & 1) != 0;
        var length = (buffer[1] << 24) | (buffer[2] << 16) | (buffer[3] << 8) | buffer[4];
        if (length < 0 || buffer.Length < 5 + length)
        {
            return false;
        }

        payload = buffer.Slice(5, length);
        return true;
    }
}
