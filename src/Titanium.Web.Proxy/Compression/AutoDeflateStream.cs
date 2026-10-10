using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     Decompresses <c>Content-Encoding: deflate</c> whichever of the two wire formats the origin used.
///     RFC 9110 says "deflate" is the zlib format (RFC 1950: a 2-byte header, the deflate data, an Adler-32
///     trailer), but a long tail of servers send bare deflate (RFC 1951), and browsers and curl accept both.
///     <see cref="DeflateStream" /> only reads the bare form and fails on a zlib body with
///     <c>InvalidDataException: The archive entry was compressed using an unsupported compression method</c>,
///     so the first two bytes are inspected to pick <see cref="ZLibStream" /> or <see cref="DeflateStream" />.
///     <para>
///         Read-only. Body decoding is driven through <c>ReadAsync</c> / <c>CopyToAsync</c>; synchronous reads
///         work only when the stream underneath supports them (the proxy's <c>LimitedStream</c> does not),
///         exactly like a plain <see cref="DeflateStream" /> over the same source.
///     </para>
/// </summary>
internal sealed class AutoDeflateStream : Stream
{
    private readonly Stream inner;
    private readonly bool leaveOpen;
    private Stream? decoder;
    private bool disposed;

    internal AutoDeflateStream(Stream inner, bool leaveOpen)
    {
        this.inner = inner;
        this.leaveOpen = leaveOpen;
    }

    /// <summary>
    ///     True when the first two bytes form a valid zlib header: deflate method (8), window size at most
    ///     32 KiB, and the 16-bit header value is a multiple of 31 (RFC 1950 section 2.2).
    /// </summary>
    internal static bool IsZLibHeader(byte cmf, byte flg) =>
        (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
            return 0;

        if (decoder == null)
        {
            var header = new byte[2];
            var have = 0;
            while (have < header.Length)
            {
                var read = await inner.ReadAsync(header.AsMemory(have), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                have += read;
            }

            decoder = CreateDecoder(header, have);
        }

        return await decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
            return 0;

        if (decoder == null)
        {
            var header = new byte[2];
            var have = 0;
            while (have < header.Length)
            {
                var read = inner.Read(header, have, header.Length - have);
                if (read == 0)
                    break;
                have += read;
            }

            decoder = CreateDecoder(header, have);
        }

        return decoder.Read(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private Stream CreateDecoder(byte[] header, int have)
    {
        var zlib = have == 2 && IsZLibHeader(header[0], header[1]);

        // The bytes consumed for sniffing are replayed in front of the rest of the body.
        var replay = new PrefixedStream(header.AsMemory(0, have).ToArray(), inner);
        return zlib
            ? new ZLibStream(replay, CompressionMode.Decompress, leaveOpen: false)
            : new DeflateStream(replay, CompressionMode.Decompress, leaveOpen: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            decoder?.Dispose();
            if (!leaveOpen)
                inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            if (decoder != null)
                await decoder.DisposeAsync().ConfigureAwait(false);
            if (!leaveOpen)
                await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Serves a few already-read bytes, then the rest of the source. Disposing it never disposes the
    ///     source, which stays owned by <see cref="AutoDeflateStream" />.
    /// </summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] prefix;
        private readonly Stream rest;
        private int prefixOffset;

        internal PrefixedStream(byte[] prefix, Stream rest)
        {
            this.prefix = prefix;
            this.rest = rest;
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

        private int TakePrefix(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, prefix.Length - prefixOffset);
            prefix.AsSpan(prefixOffset, n).CopyTo(buffer);
            prefixOffset += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (prefixOffset < prefix.Length)
                return new ValueTask<int>(TakePrefix(buffer.Span));

            return rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer) =>
            prefixOffset < prefix.Length ? TakePrefix(buffer) : rest.Read(buffer);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
