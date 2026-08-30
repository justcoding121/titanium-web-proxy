using System;
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
        if (isChunked)
            bytesRemaining = 0;
        else
            bytesRemaining = contentLength == -1 ? long.MaxValue : contentLength;
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
        // null = EOF; empty = blank line (half-closed / framing glitch). Either way there is no
        // more chunk payload — treat as end rather than PROTOCOL_ERROR via Invalid chunk length: ''.
        if (string.IsNullOrEmpty(chunkHead))
        {
            bytesRemaining = -1;
            return;
        }

        if (!ChunkSizeParser.TryParse(chunkHead, ProxyLimits.DefaultMaxChunkSizeBytes, out var chunkSize))
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

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
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

        var toRead = (int)Math.Min(buffer.Length, bytesRemaining);
        int res;

        // Prefer reading straight into an array-backed destination to avoid a rent+copy.
        // When the destination is not array-backed, rent a pool buffer and copy — never allocate
        // a temporary larger than the pool size; slice the read instead.
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment) &&
            segment.Array != null)
        {
            res = await baseReader.ReadAsync(segment.Array, segment.Offset, toRead, cancellationToken);
        }
        else
        {
            var rented = bufferPool.GetBuffer();
            try
            {
                var slice = Math.Min(toRead, rented.Length);
                res = await baseReader.ReadAsync(rented, 0, slice, cancellationToken);
                rented.AsMemory(0, res).CopyTo(buffer);
            }
            finally
            {
                bufferPool.ReturnBuffer(rented);
            }
        }

        bytesRemaining -= res;

        if (res == 0) bytesRemaining = -1;

        return res;
    }

    public async Task Finish()
    {
        // Exact Content-Length drain leaves bytesRemaining == 0; nothing left to syphon.
        // The previous unconditional loop rented an 8 KiB buffer and issued a dummy read —
        // a per-response tax on every tiny-GET MITM / bridge path.
        if (bytesRemaining is -1 or 0)
            return;

        // Drain any unread framing bytes so the underlying keep-alive connection stays aligned.
        // (Previously this threw when leftover payload remained after decompression, which is
        // exactly the case Finish must clean up.)
        var buffer = bufferPool.GetBuffer();
        try
        {
            while (bytesRemaining != -1)
            {
                var res = await ReadAsync(buffer.AsMemory(), CancellationToken.None);
                if (res == 0) break;
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}