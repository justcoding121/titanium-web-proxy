namespace Titanium.Web.Proxy.Compression
{
    class CompressionFactory
    {
        public ICompression Create(string type)
        {
            switch (type)
            {
                case "gzip":
                    return new GZipCompression();
                case "deflate":
                    return new DeflateCompression();
                case "zlib":
                    return new ZlibCompression();
                default:
                    return null;
            }
        }
    }
}
