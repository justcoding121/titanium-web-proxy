using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Network.Streams;

/// <summary>
///     Write-only wrapper enforcing a cumulative byte cap on an inner stream, throwing
///     <see cref="BodySizeLimitExceededException" /> the instant a write would push the running total
///     past the configured limit - unless <paramref name="mode" /> (see the constructor) says
///     otherwise, per the plan's rollout section: <see cref="PolicyFamily.BodyBudget" /> is one of the
///     families that supports <see cref="PolicyMode.Observe" />.
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
    private readonly PolicyMode mode;
    private long totalWritten;
    private bool breachRecorded;

    /// <summary>
    ///     <paramref name="maxBytes" /> of zero or negative means unlimited, matching the convention
    ///     already used by <see cref="BoundedBodyPipe" /> and <c>ProxyServer.MaxBufferedBodyBytes</c>.
    ///     <paramref name="mode" /> defaults to <see cref="PolicyMode.Enforce" /> - today's shipped
    ///     behavior - for call sites that have not been migrated to read a live
    ///     <c>ProxyServer.PolicyModes</c> value. Under <see cref="PolicyMode.Observe" />, a breach is
    ///     recorded (metric plus one log line, the first time only) but the write still succeeds and
    ///     the running total keeps advancing past <paramref name="maxBytes" /> unbounded. Under
    ///     <see cref="PolicyMode.Disabled" />, the limit is never consulted at all, identical to
    ///     passing zero.
    /// </summary>
    internal BoundedWriteStream(Stream inner, long maxBytes, PolicyMode mode = PolicyMode.Enforce)
    {
        this.inner = inner;
        this.maxBytes = maxBytes;
        this.mode = mode;
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
        if (maxBytes <= 0 || mode == PolicyMode.Disabled) return;

        var newTotal = totalWritten + additionalBytes;
        if (newTotal > maxBytes)
        {
            if (!breachRecorded)
            {
                breachRecorded = true;
                ProxyMetrics.PolicyBreach(PolicyFamily.BodyBudget, mode);
            }

            if (mode == PolicyMode.Enforce)
                throw new BodySizeLimitExceededException(
                    $"Body byte count {newTotal:N0} exceeds the configured limit of {maxBytes:N0} bytes.");
        }

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
