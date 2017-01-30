namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///  A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal class CompressionFactory
    {
        public ICompression Create(string type)
        {
            switch (type)
            {
                case "gzip":
                    return new GZipCompression();
                case "deflate":
                    return new DeflateCompression();
                default:
                    return null;
            }
        }
    }
}
