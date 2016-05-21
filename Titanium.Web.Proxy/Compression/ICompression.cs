using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    interface ICompression
    {
        Task<byte[]> Compress(byte[] responseBody);
    }
}
