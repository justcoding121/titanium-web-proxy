using System;
using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal static class CompressionFactory
    {
        internal static Stream Create(string type, Stream stream, bool leaveOpen = true)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return new GZipStream(stream, CompressionMode.Compress, leaveOpen);
                case KnownHeaders.ContentEncodingDeflate:
                    return new DeflateStream(stream, CompressionMode.Compress, leaveOpen);
                case KnownHeaders.ContentEncodingBrotli:
                    if (!RunTime.IsWindows)
                    {
                        throw new PlatformNotSupportedException("BrotliSharpLib currently supports only Windows.");
                    }

                    return new BrotliSharpLib.BrotliStream(stream, CompressionMode.Compress, leaveOpen);
                default:
                    throw new Exception($"Unsupported compression mode: {type}");
            }
        }
    }
}
