#pragma warning disable CA1416
using System;
using System.Buffers;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Copies a known-length HTTP/1.1 request body onto an HTTP/3 origin stream.
///     The client socket is left at the next request line.
/// </summary>
internal static class Http3RequestBodyForwarder
{
    internal static async Task CopyContentLengthAsync(Stream client, long contentLength, QuicStream origin,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var left = contentLength;
            while (left > 0)
            {
                var want = (int)Math.Min(buffer.Length, left);
                var read = await client.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await Http3Frame.WriteAsync(origin, Http3FrameType.Data, buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                left -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
