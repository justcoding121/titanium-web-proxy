using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Titanium.Web.Proxy.Helpers
{
    public class CompressionHelper
    {
        private static readonly int BUFFER_SIZE = 8192;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressZlib(byte[] bytes)
        {

            using (MemoryStream ms = new MemoryStream())
            {
                using (Ionic.Zlib.ZlibStream zip = new Ionic.Zlib.ZlibStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressDeflate(byte[] bytes)
        {

            using (MemoryStream ms = new MemoryStream())
            {
                using (Ionic.Zlib.DeflateStream zip = new Ionic.Zlib.DeflateStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressGzip(byte[] bytes)
        {

            using (MemoryStream ms = new MemoryStream())
            {
                using (Ionic.Zlib.GZipStream zip = new Ionic.Zlib.GZipStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
            }

        }

        public static byte[] DecompressGzip(Stream input)
        {
            using (var decompressor = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            {

                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
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
            using (Ionic.Zlib.DeflateStream decompressor = new Ionic.Zlib.DeflateStream(input, Ionic.Zlib.CompressionMode.Decompress))
            {
                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
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
            using (Ionic.Zlib.ZlibStream decompressor = new Ionic.Zlib.ZlibStream(input, Ionic.Zlib.CompressionMode.Decompress))
            {
                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
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
