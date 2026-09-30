using System.Collections.Immutable;

namespace Titanium.Inspector.Services;

/// <summary>
/// Publishes live WebSocket frames as immutable snapshots so UI / spill readers
/// never enumerate a list that a proxy thread is still appending to.
/// </summary>
internal sealed class LiveWebSocketFrames
{
    private readonly object _gate = new();
    private ImmutableList<WebSocketFrameSnapshot> _frames = ImmutableList<WebSocketFrameSnapshot>.Empty;

    public IReadOnlyList<WebSocketFrameSnapshot> Current
    {
        get
        {
            lock (_gate)
            {
                return _frames;
            }
        }
    }

    /// <summary>
    /// Append <paramref name="frame"/> and assign the new immutable list to
    /// <paramref name="snap"/>.<see cref="SessionSnapshot.WebSocketFrames"/>.
    /// </summary>
    public void Append(SessionSnapshot snap, WebSocketFrameSnapshot frame)
    {
        lock (_gate)
        {
            _frames = _frames.Add(frame);
            snap.WebSocketFrames = _frames;
        }
    }
}
