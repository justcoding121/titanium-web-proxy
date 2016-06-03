using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    internal class DefaultDecompression : IDecompression
    {
        public Task<byte[]> Decompress(byte[] compressedArray)
        {
            return Task.FromResult(compressedArray);
        }
    }
}
