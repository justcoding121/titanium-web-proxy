﻿using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// concrete implementation of deflate de-compression
    /// </summary>
    internal class DeflateDecompression : IDecompression
    {
        public async Task<byte[]> Decompress(byte[] compressedArray, int bufferSize)
        {
            using (var stream = new MemoryStream(compressedArray))
            using (var decompressor = new DeflateStream(stream, CompressionMode.Decompress))
            {
                var buffer = new byte[bufferSize];

                using (var output = new MemoryStream())
                {
                    int read;
                    while ((read = await decompressor.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
        }
    }
}
