using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    interface IDecompression
    {
        Task<byte[]> Decompress(byte[] compressedArray);
    }
}
