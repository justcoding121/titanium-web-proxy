namespace Titanium.Web.Proxy.Decompression
{
    internal class DecompressionFactory
    {
        internal IDecompression Create(string type)
        {
            switch(type)
            {
                case "gzip":
                    return new GZipDecompression();
                case "deflate":
                    return new DeflateDecompression();
                case "zlib":
                    return new ZlibDecompression();
                default:
                    return new DefaultDecompression();
            }
        }
    }
}
