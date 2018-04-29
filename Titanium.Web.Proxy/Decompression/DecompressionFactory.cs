using System;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    ///     A factory to generate the de-compression methods based on the type of compression
    /// </summary>
    internal class DecompressionFactory
    {
        //cache
        private static readonly IDecompression gzip = new GZipDecompression();

        private static readonly IDecompression deflate = new DeflateDecompression();

        internal static IDecompression Create(string type)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return gzip;
                case KnownHeaders.ContentEncodingDeflate:
                    return deflate;
                default:
                    throw new Exception($"Unsupported decompression mode: {type}");
            }
        }
    }
}
