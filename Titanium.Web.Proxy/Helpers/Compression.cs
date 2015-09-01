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


        public static string DecompressGzip(Stream input, Encoding e)
        {
            using (System.IO.Compression.GZipStream decompressor = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            {

                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return e.GetString(output.ToArray());
                }

            }

        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public static byte[] CompressZlib(string responseData, Encoding e)
        {

            Byte[] bytes = e.GetBytes(responseData);

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
        public static byte[] CompressDeflate(string responseData, Encoding e)
        {
            Byte[] bytes = e.GetBytes(responseData);

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
        public static byte[] CompressGzip(string responseData, Encoding e)
        {
            Byte[] bytes = e.GetBytes(responseData);

            using (MemoryStream ms = new MemoryStream())
            {
                using (Ionic.Zlib.GZipStream zip = new Ionic.Zlib.GZipStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();



            }

        }
        public static string DecompressDeflate(Stream input, Encoding e)
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
                    return e.GetString(output.ToArray());
                }

            }
        }
        public static string DecompressZlib(Stream input, Encoding e)
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
                    return e.GetString(output.ToArray());
                }
            }

        }
    }
}
