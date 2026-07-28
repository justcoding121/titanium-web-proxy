using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

internal class LimitedStream : Stream
{
    private readonly IHttpStreamReader baseReader;
    private readonly IBufferPool bufferPool;
    private readonly bool isChunked;
    private readonly HeaderCollection? trailingHeaders;
    private long bytesRemaining;

    private bool readChunkTrail;

    /// <param name="baseStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="isChunked"></param>
    /// <param name="contentLength"></param>
    /// <param name="trailingHeaders">
    ///     Optional collection to populate with the chunked body's trailer headers, if any (ignored when
    ///     <paramref name="isChunked" /> is false). Used so buffered/decompressing whole-body reads still
    ///     populate a request/response's trailing headers the same way the pass-through relay path does.
    /// </param>
    internal LimitedStream(IHttpStreamReader baseStream, IBufferPool bufferPool, bool isChunked,
        long contentLength, HeaderCollection? trailingHeaders = null)
    {
        baseReader = baseStream;
        this.bufferPool = bufferPool;
        this.isChunked = isChunked;
        this.trailingHeaders = trailingHeaders;
        bytesRemaining = isChunked
            ? 0
            : contentLength == -1
                ? long.MaxValue
                : contentLength;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private async Task GetNextChunkAsync()
    {
        if (readChunkTrail)
        {
            // read the chunk trail of the previous chunk
            var s = await baseReader.ReadLineAsync();
            if (s == null)
            {
                bytesRemaining = -1;
                return;
            }
        }

        readChunkTrail = true;

        var chunkHead = await baseReader.ReadLineAsync();
        if (chunkHead == null)
        {
            bytesRemaining = -1;
            return;
        }

        var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
        if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

        if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
            throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

        bytesRemaining = chunkSize;

        if (chunkSize == 0)
        {
            bytesRemaining = -1;

            // Trailer header block, strictly through the terminating blank line (see ChunkedTrailerHelper) -
            // reading only a single line here (as before) left any additional trailer lines unread on the
            // source, corrupting a pooled keep-alive connection's next message.
            await ChunkedTrailerHelper.ReadTrailingHeaders(baseReader, trailingHeaders ?? new HeaderCollection(),
                null);
        }
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use ReadAsync.");
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (bytesRemaining == -1) return 0;

        if (bytesRemaining == 0)
        {
            if (isChunked)
                await GetNextChunkAsync();
            else
                bytesRemaining = -1;
        }

        if (bytesRemaining == -1) return 0;

        var toRead = (int)Math.Min(count, bytesRemaining);
        var res = await baseReader.ReadAsync(buffer, offset, toRead, cancellationToken);
        bytesRemaining -= res;

        if (res == 0) bytesRemaining = -1;

        return res;
    }

    public async Task Finish()
    {
        if (bytesRemaining != -1)
        {
            var buffer = bufferPool.GetBuffer();
            try
            {
                var res = await ReadAsync(buffer, 0, buffer.Length);
                if (res != 0) throw new Exception("Data received after stream end");
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}