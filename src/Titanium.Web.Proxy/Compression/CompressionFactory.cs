using System;
using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Abstractions.Compression;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal static class CompressionFactory
    {
        /// <summary>Compressor with an explicit level (response compression); gzip and Brotli only.</summary>
        internal static Stream Create(HttpCompression type, Stream stream, CompressionLevel level, bool leaveOpen = true)
        {
            return type switch
            {
                HttpCompression.Gzip => new GZipStream(stream, level, leaveOpen),
                HttpCompression.Brotli => new BrotliStream(stream, level, leaveOpen),
                HttpCompression.Zstd when ContentCodecs.TryGet("zstd", out var codec) && codec is not null
                    => codec.CreateCompressor(stream, level, leaveOpen),
                _ => throw new NotSupportedException($"Unsupported compression mode: {type}")
            };
        }

        internal static Stream Create(HttpCompression type, Stream stream, bool leaveOpen = true)
        {
            return type switch
            {
                HttpCompression.Gzip => new GZipStream(stream, CompressionMode.Compress, leaveOpen),
                HttpCompression.Deflate => new DeflateStream(stream, CompressionMode.Compress, leaveOpen),
                // System.IO.Compression.BrotliStream (not BrotliSharpLib) for the same reason as
                // DecompressionFactory: it has genuine async Read/Write support, unlike
                // BrotliSharpLib which falls back to synchronous calls on the wrapped stream.
                HttpCompression.Brotli => new BrotliStream(stream, CompressionMode.Compress, leaveOpen),
                _ => throw new NotSupportedException($"Unsupported compression mode: {type}")
            };
        }
    }
}