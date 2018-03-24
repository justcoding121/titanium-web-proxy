using System;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///  A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal class CompressionFactory
    {
        //cache
        private static readonly Lazy<ICompression> gzip = new Lazy<ICompression>(() => new GZipCompression());
        private static readonly Lazy<ICompression> deflate = new Lazy<ICompression>(() => new DeflateCompression());

        public static ICompression GetCompression(string type)
        {
            switch (type)
            {
                case "gzip":
                    return gzip.Value;
                case "deflate":
                    return deflate.Value;
                default:
                    return null;
            }
        }
    }
}
