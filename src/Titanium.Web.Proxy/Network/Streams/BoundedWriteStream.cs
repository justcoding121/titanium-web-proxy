using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Streams;

/// <summary>
///     Write-only wrapper enforcing a cumulative byte cap on an inner stream, throwing
///     <see cref="BodySizeLimitExceededException" /> the instant a write would push the running total
///     past the configured limit.
///     <para>
///         Exists for whole-body-into-memory buffering paths that have no cumulative limit of their
///         own today: <c>SessionEventArgs.ReadBodyAsync</c> (HTTP/1 <c>GetRequestBody</c>/<c>GetResponseBody</c>)
///         and native HTTP/2 client-facing body interception in <c>Http2Helper</c>. Both only bound an
///         individual chunk or DATA frame - per the hardening plan, "per-frame limits are not cumulative
///         limits" - so an attacker sending many small chunks/frames could otherwise accumulate an
///         unbounded in-memory body. <see cref="Network.Streams.BoundedBodyPipe" /> already solves the
///         same problem for the HTTP/2-to-origin body-streaming path; this type gives the whole-body
///         MemoryStream paths the same cumulative guarantee without adopting a full pipe.
///     </para>
/// </summary>
internal sealed class BoundedWriteStream : Stream
{
    private readonly Stream inner;
    private readonly long maxBytes;
    private long totalWritten;

    /// <summary>
    ///     <paramref name="maxBytes" /> of zero or negative means unlimited, matching the convention
    ///     already used by <see cref="BoundedBodyPipe" /> and <c>ProxyServer.MaxBufferedBodyBytes</c>.
    /// </summary>
    internal BoundedWriteStream(Stream inner, long maxBytes)
    {
        this.inner = inner;
        this.maxBytes = maxBytes;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("BoundedWriteStream is write-only.");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    ///     Advances the running total and throws before any byte of an over-limit write reaches
    ///     <see cref="inner" />, so a caller that catches <see cref="BodySizeLimitExceededException" />
    ///     never observes a partially-written, truncated body as if it were complete.
    /// </summary>
    private void CheckAndAdvance(int additionalBytes)
    {
        if (maxBytes <= 0) return;

        var newTotal = totalWritten + additionalBytes;
        if (newTotal > maxBytes)
            throw new BodySizeLimitExceededException(
                $"Body byte count {newTotal:N0} exceeds the configured limit of {maxBytes:N0} bytes.");

        totalWritten = newTotal;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckAndAdvance(count);
        inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        CheckAndAdvance(count);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckAndAdvance(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }
}
