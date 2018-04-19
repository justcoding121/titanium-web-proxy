using System;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal static class CompressionFactory
    {
        //cache
        private static readonly ICompression gzip = new GZipCompression();
        private static readonly ICompression deflate = new DeflateCompression();

        public static ICompression GetCompression(string type)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return gzip;
                case KnownHeaders.ContentEncodingDeflate:
                    return deflate;
                default:
                    throw new Exception($"Unsupported compression mode: {type}");
            }
        }
    }
}
