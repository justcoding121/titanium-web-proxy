using System;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///  A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal class CompressionFactory
    {
        public static readonly CompressionFactory Instance = new CompressionFactory();
        
        //cache
        private static readonly Lazy<ICompression> gzip = new Lazy<ICompression>(() => new GZipCompression());
        private static readonly Lazy<ICompression> deflate = new Lazy<ICompression>(() => new DeflateCompression());
        private static readonly Lazy<ICompression> def = new Lazy<ICompression>(() => new DefaultCompression());

        public static ICompression GetCompression(string type)
        {
            switch (type)
            {
                case KnownHeaders.ContentEncodingGzip:
                    return gzip.Value;
                case KnownHeaders.ContentEncodingDeflate:
                    return deflate.Value;
                default:
                    return def.Value;
            }
        }
    }
}
