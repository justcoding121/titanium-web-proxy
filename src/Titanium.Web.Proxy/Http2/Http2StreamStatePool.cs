using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Bounded pool of <see cref="Http2StreamState" /> shells reused across streams on one connection
///     (HTTP/2 stream state pooling pattern). Caps retained instances to avoid unbounded growth.
/// </summary>
internal sealed class Http2StreamStatePool
{
    private readonly ConcurrentBag<Http2StreamState> pool = new();
    private readonly int maxRetained;
    private int retained;

    public Http2StreamStatePool(int maxRetained)
    {
        this.maxRetained = Math.Max(1, maxRetained);
    }

    public Http2StreamState RentCompressed(int streamId)
    {
        if (pool.TryTake(out var state))
        {
            Interlocked.Decrement(ref retained);
            state.ResetForCompressedRelay(streamId);
            return state;
        }

        return new Http2StreamState(streamId);
    }

    public Http2StreamState RentSession(int streamId, SessionEventArgs sessionArgs)
    {
        if (pool.TryTake(out var state))
        {
            Interlocked.Decrement(ref retained);
            state.ResetForSession(streamId, sessionArgs);
            return state;
        }

        return new Http2StreamState(streamId, sessionArgs);
    }

    public void Return(Http2StreamState state)
    {
        state.PrepareForPool();
        if (Volatile.Read(ref retained) >= maxRetained)
            return;

        pool.Add(state);
        Interlocked.Increment(ref retained);
    }
}
