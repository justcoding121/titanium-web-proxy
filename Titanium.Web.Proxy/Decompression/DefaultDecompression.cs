using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    class DefaultDecompression : IDecompression
    {
        public Task<byte[]> Decompress(byte[] compressedArray)
        {
            return Task.FromResult(compressedArray);
        }
    }
}
