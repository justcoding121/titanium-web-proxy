namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// A factory to generate the de-compression methods based on the type of compression
    /// </summary>
    internal class DecompressionFactory
    {
        public static DecompressionFactory Instance = new DecompressionFactory();

        internal IDecompression Create(string type)
        {
            switch (type)
            {
                case "gzip":
                    return new GZipDecompression();
                case "deflate":
                    return new DeflateDecompression();
                default:
                    return new DefaultDecompression();
            }
        }
    }
}
