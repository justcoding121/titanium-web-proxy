using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Drains an HTTP/2 body channel and, when the request declares one gzip or Brotli coding,
    ///     replaces the body with the decompressed bytes. The channel is always drained so the
    ///     frame reader is not left waiting. Extended CONNECT is left unchanged.
    /// </summary>
    /// <returns>False when the client has already been given a 400 or 413.</returns>
    private async Task<bool> TryDecompressChannelBodyAsync(SessionEventArgs session,
        Channel<(byte[] Buffer, int Length)> channel, CancellationToken cancellationToken)
    {
        var policy = RequestDecompressionPolicy;
        var request = session.HttpClient.Request;
        if (policy is null || request.ExtendedConnectProtocol is not null
            || !Compression.RequestDecompressionPolicy.TryGetEncoding(request, out var encoding))
        {
            return true;
        }

        var compressed = new List<byte>();
        var tooLarge = false;
        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (tooLarge || compressed.Count + chunk.Length > policy.MaxDecompressedBytes)
                {
                    tooLarge = true;
                    continue;
                }

                if (chunk.Length > 0)
                    compressed.AddRange(chunk.Buffer.AsSpan(0, chunk.Length));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }

        if (tooLarge)
        {
            session.GenericResponse("request body could not be decompressed", HttpStatusCode.RequestEntityTooLarge);
            return false;
        }

        var outcome = policy.TryDecompress(encoding, compressed.ToArray(), out var plain);
        if (outcome != RequestDecompressionOutcome.Decompressed)
        {
            session.GenericResponse("request body could not be decompressed",
                outcome == RequestDecompressionOutcome.Corrupt
                    ? HttpStatusCode.BadRequest
                    : HttpStatusCode.RequestEntityTooLarge);
            return false;
        }

        request.Headers.RemoveHeader(KnownHeaders.ContentEncoding);
        request.Body = plain;
        request.ContentLength = plain.Length;
        request.IsBodyRead = true;
        request.IsBodyReceived = true;
        Diagnostics.ProxyMetrics.BodyDecompressed(plain.Length, "request");
        return true;
    }
}
