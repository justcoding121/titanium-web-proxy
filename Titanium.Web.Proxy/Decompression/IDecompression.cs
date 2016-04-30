using System.IO;

namespace Titanium.Web.Proxy.Decompression
{
    interface IDecompression
    {
        byte[] Decompress(byte[] compressedArray);
    }
}
