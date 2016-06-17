using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// An inteface for http compression
    /// </summary>
    interface ICompression
    {
        Task<byte[]> Compress(byte[] responseBody);
    }
}
