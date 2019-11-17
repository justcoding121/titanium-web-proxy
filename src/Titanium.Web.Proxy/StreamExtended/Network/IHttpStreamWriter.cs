using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network
{
    /// <summary>
    ///     A concrete implementation of this interface is required when calling CopyStream.
    /// </summary>
    internal interface IHttpStreamWriter
    {
        void Write(byte[] buffer, int i, int bufferLength);

        Task WriteAsync(byte[] buffer, int i, int bufferLength, CancellationToken cancellationToken);

        Task CopyBodyAsync(IHttpStreamReader streamReader, bool isChunked, long contentLength,
            Action<byte[], int, int>? onCopy, CancellationToken cancellationToken);
    }
}
