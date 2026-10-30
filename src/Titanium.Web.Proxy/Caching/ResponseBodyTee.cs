using System;
using System.Buffers;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Accumulates a streamed response body while it is relayed to the client, so it can be cached afterwards. It stops
/// accumulating (and frees its buffer) the moment the body outgrows its limit; the relay is never affected either way.
/// The staging buffer is rented from <see cref="ArrayPool{T}.Shared"/> and always returned.
/// </summary>
internal sealed class ResponseBodyTee : IDisposable
{
    private readonly int _limit;
    private byte[]? _buffer;
    private int _length;

    public ResponseBodyTee(int limit, int expectedLength = -1)
    {
        _limit = limit;
        var initial = expectedLength > 0 ? Math.Min(expectedLength, limit) : Math.Min(limit, 16 * 1024);
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initial, 256));
    }

    /// <summary>True once more than the limit arrived: nothing is kept and the body is not cacheable.</summary>
    public bool Overflowed { get; private set; }

    public int Length => _length;

    public void Append(byte[] source, int offset, int count)
    {
        if (Overflowed || _buffer is null || count <= 0)
        {
            return;
        }

        if ((long)_length + count > _limit)
        {
            Overflowed = true;
            Release();
            return;
        }

        var needed = _length + count;
        if (needed > _buffer.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Min(_limit, Math.Max(needed, _buffer.Length * 2)));
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        Buffer.BlockCopy(source, offset, _buffer, _length, count);
        _length = needed;
    }

    /// <summary>The accumulated body as an exact-size array, or null when it overflowed.</summary>
    public byte[]? ToArray()
    {
        if (Overflowed || _buffer is null)
        {
            return null;
        }

        var result = GC.AllocateUninitializedArray<byte>(_length);
        if (_length > 0)
        {
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
        }

        return result;
    }

    public void Dispose() => Release();

    private void Release()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// The single per-session slot the response cache hangs its state on, so a session carries one reference that is
/// null whenever the cache is off. Disposing the session releases any waiters and the staging buffer.
/// </summary>
internal sealed class CacheSessionSlot : IDisposable
{
    public CacheRequestState? State;
    public ResponseBodyTee? Tee;
    public StorePlanHolder? Plan;

    /// <summary>Start collecting the body that is about to be relayed, to store it under <paramref name="plan"/>.</summary>
    public void BeginTee(CacheRequestState state, HttpCachePolicy.StorePlan plan, long expectedLength)
    {
        State = state;
        Tee?.Dispose();
        Tee = new ResponseBodyTee(state.Owner.MaxEntryBytes, expectedLength > 0 && expectedLength <= int.MaxValue ? (int)expectedLength : -1);
        Plan = new StorePlanHolder { Plan = plan, ExpectedLength = expectedLength };
    }

    /// <summary>The caller already holds the whole body (a coalesced read): store it instead of using the tee.</summary>
    public void StoreBuffered(byte[] body)
    {
        var tee = Tee;
        Tee = null;
        tee?.Dispose();
        if (State is { } state && Plan is { } holder)
        {
            state.Owner.CompleteStore(state, holder.Plan, body, holder.ExpectedLength);
        }
    }

    /// <summary>Called once the body was relayed in full: store it, or drop it when it outgrew the limit.</summary>
    public void FinishStore()
    {
        var tee = Tee;
        Tee = null;
        if (tee is null || State is not { } state || Plan is not { } holder)
        {
            tee?.Dispose();
            return;
        }

        byte[]? body;
        try
        {
            body = tee.ToArray();
        }
        finally
        {
            tee.Dispose();
        }

        state.Owner.CompleteStore(state, holder.Plan, body, holder.ExpectedLength);
    }

    public void Dispose()
    {
        Tee?.Dispose();
        Tee = null;
        State?.Abandon();
    }
}

/// <summary>Carries the store plan between the response-header decision and the end of the body copy.</summary>
internal sealed class StorePlanHolder
{
    public required HttpCachePolicy.StorePlan Plan { get; init; }
    public long ExpectedLength { get; init; } = -1;
}
