using System.Collections.ObjectModel;

namespace Titanium.Inspector.Services;

/// <summary>Session registry with LRU eviction at 50k.</summary>
public sealed class SessionRegistry
{
    private const int LruLimit = 50_000;
    private readonly object _gate = new();
    private readonly LinkedList<long> _lru = new();
    private readonly Dictionary<long, LinkedListNode<long>> _nodes = new();
    private readonly Dictionary<long, WeakReference<SessionSnapshot>> _weak = new();

    public ObservableCollection<SessionSnapshot> VisibleSessions { get; } = new();

    public void Add(SessionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(snapshot.Id, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
            }
            else
            {
                var node = _lru.AddFirst(snapshot.Id);
                _nodes[snapshot.Id] = node;
                _weak[snapshot.Id] = new WeakReference<SessionSnapshot>(snapshot);
                VisibleSessions.Add(snapshot);
            }

            while (_lru.Count > LruLimit)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _nodes.Remove(last.Value);
                _weak.Remove(last.Value);
                for (var i = VisibleSessions.Count - 1; i >= 0; i--)
                {
                    if (VisibleSessions[i].Id == last.Value)
                    {
                        VisibleSessions.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }

    public SessionSnapshot? TryGet(long id)
    {
        lock (_gate)
        {
            if (_weak.TryGetValue(id, out var wr) && wr.TryGetTarget(out var snap))
            {
                return snap;
            }

            return null;
        }
    }
}
