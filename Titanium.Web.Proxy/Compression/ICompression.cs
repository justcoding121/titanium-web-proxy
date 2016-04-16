namespace Titanium.Web.Proxy.Compression
{
    interface ICompression
    {
        byte[] Compress(byte[] responseBody);
    }
}
