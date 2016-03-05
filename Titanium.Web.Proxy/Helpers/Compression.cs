using System.Diagnostics.CodeAnalysis;
using System.IO;
using Ionic.Zlib;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// A helper to handle compression/decompression (gzip, zlib & deflate)
    /// </summary>
    public class CompressionHelper
    {
        /// <summary>
        /// compress the given bytes using zlib compression 
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
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

        /// <summary>
        /// compress the given bytes using deflate compression
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
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

        /// <summary>
        /// compress the given bytes using gzip compression
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
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

       /// <summary>
       /// decompression the gzip compressed byte array
       /// </summary>
       /// <param name="gzip"></param>
       /// <returns></returns>
        //identify why passing stream instead of bytes returns empty result
        public static byte[] DecompressGzip(byte[] gzip)
        {
            using (var decompressor = new System.IO.Compression.GZipStream(new MemoryStream(gzip), System.IO.Compression.CompressionMode.Decompress))
            {
                var buffer = new byte[ProxyServer.BUFFER_SIZE];

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

        /// <summary>
        /// decompress the deflate byte stream
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static byte[] DecompressDeflate(Stream input)
        {
            using (var decompressor = new DeflateStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[ProxyServer.BUFFER_SIZE];

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

        /// <summary>
        /// decompress the zlib byte stream
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static byte[] DecompressZlib(Stream input)
        {
            using (var decompressor = new ZlibStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[ProxyServer.BUFFER_SIZE];

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