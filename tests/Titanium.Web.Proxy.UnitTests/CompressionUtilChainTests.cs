using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CompressionUtilChainTests
{
    [TestMethod]
    public void CompressionFactory_Create_KnownKinds_RoundTripThroughDecompressionFactory()
    {
        var plain = Encoding.UTF8.GetBytes("factory-bytes");
        foreach (var kind in new[] { HttpCompression.Gzip, HttpCompression.Deflate, HttpCompression.Brotli })
        {
            using var compressed = new MemoryStream();
            using (var encoder = CompressionFactory.Create(kind, compressed, leaveOpen: true))
                encoder.Write(plain);

            compressed.Position = 0;
            using var decoder = DecompressionFactory.Create(kind, compressed, leaveOpen: true);
            using var decoded = new MemoryStream();
            decoder.CopyTo(decoded);
            CollectionAssert.AreEqual(plain, decoded.ToArray(), $"Failed for {kind}");
        }
    }

    [TestMethod]
    public void CompressionFactory_Unsupported_Throws()
    {
        using var ms = new MemoryStream();
        Assert.ThrowsExactly<NotSupportedException>(() => CompressionFactory.Create(HttpCompression.Unsupported, ms));
        Assert.ThrowsExactly<NotSupportedException>(() => DecompressionFactory.Create(HttpCompression.Unsupported, ms));
    }

    [TestMethod]
    public void CreateDecompressionChain_EmptyOrWhitespace_Passthrough()
    {
        using var inner = new MemoryStream(Encoding.UTF8.GetBytes("plain"));
        var (stream, owned) = CompressionUtil.CreateDecompressionChain(inner, "   ");
        Assert.AreSame(inner, stream);
        Assert.AreEqual(0, owned.Count);
    }

    [TestMethod]
    public void CreateDecompressionChain_UnsupportedLayer_ReturnsInnerUnchanged()
    {
        using var inner = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var (stream, owned) = CompressionUtil.CreateDecompressionChain(inner, "gzip, exotic");
        Assert.AreSame(inner, stream);
        Assert.AreEqual(0, owned.Count, "Unsupported stacked encodings must not partially wrap.");
    }

    [TestMethod]
    public void CreateDecompressionChain_StackedGzipDeflate_AppliesInReverseOrder()
    {
        var plain = Encoding.UTF8.GetBytes("stacked-body");

        // Content-Encoding: gzip, deflate → applied gzip then deflate → wire is deflate(gzip(plain)).
        byte[] gzipped;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(plain);
            gzipped = ms.ToArray();
        }

        byte[] wire;
        using (var ms = new MemoryStream())
        {
            using (var deflate = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(gzipped);
            wire = ms.ToArray();
        }

        using var inner = new MemoryStream(wire);
        var (stream, owned) = CompressionUtil.CreateDecompressionChain(inner, "gzip, deflate");
        try
        {
            Assert.AreEqual(2, owned.Count);
            using var reader = new MemoryStream();
            stream.CopyTo(reader);
            CollectionAssert.AreEqual(plain, reader.ToArray());
        }
        finally
        {
            foreach (var layer in owned)
                layer.Dispose();
        }
    }
}
