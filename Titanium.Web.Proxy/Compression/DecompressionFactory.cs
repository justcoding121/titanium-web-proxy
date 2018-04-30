using System;
using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     A factory to generate the de-compression methods based on the type of compression
    /// </summary>
    internal class DecompressionFactory
    {
        internal static Stream Create(string type, Stream stream, bool leaveOpen = true)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return new GZipStream(stream, CompressionMode.Decompress, leaveOpen);
                case KnownHeaders.ContentEncodingDeflate:
                    return new DeflateStream(stream, CompressionMode.Decompress, leaveOpen);
                default:
                    throw new Exception($"Unsupported decompression mode: {type}");
            }
        }
    }
}
