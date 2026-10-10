using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
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

    private static byte[] Compress(byte[] plain, Func<Stream, Stream> encoder)
    {
        using var ms = new MemoryStream();
        using (var e = encoder(ms))
            e.Write(plain);
        return ms.ToArray();
    }

    /// <summary>A forward-only stream with no length, like the proxy's body streams.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private int pos;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Hand back at most one byte per call to exercise the two-byte sniff across reads.
            if (pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[pos++];
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [TestMethod]
    public async Task Deflate_AcceptsBothZlibWrappedAndBareDeflate()
    {
        var plain = Encoding.UTF8.GetBytes(new string('x', 2000) + "-deflate-body");
        var zlib = Compress(plain, s => new ZLibStream(s, CompressionMode.Compress, true));
        var bare = Compress(plain, s => new DeflateStream(s, CompressionMode.Compress, true));

        // A plain DeflateStream rejects the zlib form; that was the production failure.
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            using var d = new DeflateStream(new MemoryStream(zlib), CompressionMode.Decompress);
            d.CopyTo(Stream.Null);
        });

        foreach (var (name, wire) in new[] { ("zlib", zlib), ("bare", bare) })
        {
            // Async path over a forward-only, one-byte-at-a-time source.
            var source = new ForwardOnlyStream(wire);
            await using (var decoder = DecompressionFactory.Create(HttpCompression.Deflate, source))
            {
                using var output = new MemoryStream();
                await decoder.CopyToAsync(output);
                CollectionAssert.AreEqual(plain, output.ToArray(), name + " (async)");
            }

            Assert.IsFalse(source.Disposed, "leaveOpen: true must leave the source open (" + name + ")");

            // Sync path over a MemoryStream.
            using var syncDecoder = DecompressionFactory.Create(HttpCompression.Deflate, new MemoryStream(wire));
            using var syncOutput = new MemoryStream();
            syncDecoder.CopyTo(syncOutput);
            CollectionAssert.AreEqual(plain, syncOutput.ToArray(), name + " (sync)");
        }
    }

    [TestMethod]
    public async Task Deflate_EmptyBody_ReadsAsEmpty_AndOwnedSourceIsDisposed()
    {
        var source = new ForwardOnlyStream([]);
        await using (var decoder = DecompressionFactory.Create(HttpCompression.Deflate, source, leaveOpen: false))
        {
            using var output = new MemoryStream();
            await decoder.CopyToAsync(output);
            Assert.AreEqual(0, output.Length);
        }

        Assert.IsTrue(source.Disposed, "leaveOpen: false must dispose the source");
    }

    [TestMethod]
    public void Deflate_GarbageStillFails_AsInvalidData()
    {
        using var decoder = DecompressionFactory.Create(HttpCompression.Deflate,
            new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.CopyTo(Stream.Null));
    }

    [TestMethod]
    public void IsZLibHeader_RecognisesCommonHeadersOnly()
    {
        Assert.IsTrue(AutoDeflateStream.IsZLibHeader(0x78, 0x01));
        Assert.IsTrue(AutoDeflateStream.IsZLibHeader(0x78, 0x9C));
        Assert.IsTrue(AutoDeflateStream.IsZLibHeader(0x78, 0xDA));
        Assert.IsFalse(AutoDeflateStream.IsZLibHeader(0x78, 0x9D)); // checksum not a multiple of 31
        Assert.IsFalse(AutoDeflateStream.IsZLibHeader(0x79, 0x9C)); // method is not deflate
        Assert.IsFalse(AutoDeflateStream.IsZLibHeader(0xFF, 0xFF));
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
