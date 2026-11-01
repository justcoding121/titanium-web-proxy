using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Compression;
using Titanium.Web.Proxy.Compression;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ContentCodecsTests
{
    [TestMethod]
    public void Register_FakeCodec_RoundTripsThroughFactories()
    {
        ContentCodecs.Register(new ReverseCodec());
        Assert.IsTrue(HttpCompressionNames.TryParse("zstd", out var kind));
        Assert.AreEqual(HttpCompression.Zstd, kind);

        var plain = Encoding.UTF8.GetBytes("hello bridge codec");
        using var compressed = new MemoryStream();
        using (var encoder = CompressionFactory.Create(kind, compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            encoder.Write(plain, 0, plain.Length);
        }

        compressed.Position = 0;
        using var decoder = DecompressionFactory.Create(kind, compressed, leaveOpen: true);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        CollectionAssert.AreEqual(plain, output.ToArray());
    }

    private sealed class ReverseCodec : IContentCodec
    {
        public string Name => "zstd";

        public Stream CreateCompressor(Stream sink, CompressionLevel level, bool leaveOpen = true) =>
            new ReverseTransformStream(sink, leaveOpen);

        public Stream CreateDecompressor(Stream source, bool leaveOpen = true) =>
            new ReverseTransformStream(source, leaveOpen);
    }

    private sealed class ReverseTransformStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;

        internal ReverseTransformStream(Stream inner, bool leaveOpen)
        {
            _inner = inner;
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            for (var i = 0; i < n; i++)
                buffer[offset + i] = (byte)~buffer[offset + i];
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var temp = new byte[count];
            for (var i = 0; i < count; i++)
                temp[i] = (byte)~buffer[offset + i];
            _inner.Write(temp, 0, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
