using System.IO;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    internal interface IDecompression
    {
       Task<byte[]> Decompress(byte[] compressedArray);
    }
}
