using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Decompression
{
    /// <summary>
    /// An interface for decompression
    /// </summary>
    internal interface IDecompression
    {
       Task<byte[]> Decompress(byte[] compressedArray, int bufferSize);
    }
}
