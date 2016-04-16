namespace Titanium.Web.Proxy.Decompression
{
    class DefaultDecompression : IDecompression
    {
        public byte[] Decompress(byte[] compressedArray)
        {
            return compressedArray;
        }
    }
}
