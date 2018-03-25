using System;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// A factory to generate the de-compression methods based on the type of compression
    /// </summary>
    internal class DecompressionFactory
    {
        //cache
        private static readonly Lazy<IDecompression> gzip = new Lazy<IDecompression>(() => new GZipDecompression());
        private static readonly Lazy<IDecompression> deflate = new Lazy<IDecompression>(() => new DeflateDecompression());

        public static IDecompression Create(string type)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return gzip.Value;
                case KnownHeaders.ContentEncodingDeflate:
                    return deflate.Value;
                default:
                    throw new Exception($"Unsupported decompression mode: {type}");
            }
        }
    }
}
