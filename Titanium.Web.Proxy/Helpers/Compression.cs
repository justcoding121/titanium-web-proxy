using System.Diagnostics.CodeAnalysis;
using System.IO;
using Ionic.Zlib;

namespace Titanium.Web.Proxy.Helpers
{
    public class CompressionHelper
    {
        private const int BufferSize = 8192;

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressZlib(byte[] bytes)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZlibStream(ms, CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressDeflate(byte[] bytes)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }
        }

        [SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressGzip(byte[] bytes)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }
        }

        public static byte[] DecompressGzip(Stream input)
        {
            using (
                var decompressor = new System.IO.Compression.GZipStream(input,
                    System.IO.Compression.CompressionMode.Decompress))
            {
                var buffer = new byte[BufferSize];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }

        public static byte[] DecompressDeflate(Stream input)
        {
            using (var decompressor = new DeflateStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[BufferSize];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }

        public static byte[] DecompressZlib(Stream input)
        {
            using (var decompressor = new ZlibStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[BufferSize];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }
    }
}