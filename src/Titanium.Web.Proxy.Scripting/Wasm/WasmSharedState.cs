namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// Shared data and shared queues for one <c>vm_id</c>, as Envoy scopes them. Every VM of every pool with that
/// <c>vm_id</c> sees the same key-value store and the same queues; plugins with no <c>vm_id</c> share the empty one.
/// A scope lives while a pool uses it.
/// </summary>
internal sealed class WasmSharedState
{
    private static readonly object Registry = new();
    private static readonly Dictionary<string, WasmSharedState> Scopes = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, WasmSharedQueue> QueuesByToken = [];
    private static int _nextToken;

    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Value, uint Cas)> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WasmSharedQueue> _queues = new(StringComparer.Ordinal);
    private readonly long _dataLimit;
    private long _dataBytes;
    private uint _nextCas;
    private int _references;

    private WasmSharedState(string vmId, WasmLimits limits)
    {
        VmId = vmId;
        _dataLimit = limits.SharedDataBytes;
        QueueLimit = limits.QueueBytes;
    }

    public string VmId { get; }

    /// <summary>Largest number of bytes one queue holds.</summary>
    public long QueueLimit { get; }

    /// <summary>Takes a reference to the scope for <paramref name="vmId"/>, creating it with <paramref name="limits"/>.</summary>
    public static WasmSharedState Acquire(string vmId, WasmLimits limits)
    {
        lock (Registry)
        {
            if (!Scopes.TryGetValue(vmId, out var scope))
            {
                scope = new WasmSharedState(vmId, limits);
                Scopes[vmId] = scope;
            }

            scope._references++;
            return scope;
        }
    }

    /// <summary>Drops a reference. The last one discards the data and the queues.</summary>
    public void Release()
    {
        lock (Registry)
        {
            if (--_references > 0)
            {
                return;
            }

            Scopes.Remove(VmId);
            lock (_gate)
            {
                foreach (var queue in _queues.Values)
                {
                    QueuesByToken.Remove(queue.Token);
                }

                _queues.Clear();
                _data.Clear();
                _dataBytes = 0;
            }
        }
    }

    /// <summary><c>proxy_get_shared_data</c>: the value and its CAS, or NotFound.</summary>
    public int Get(string key, out byte[] value, out uint cas)
    {
        lock (_gate)
        {
            if (_data.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                cas = entry.Cas;
                return WasmResult.Ok;
            }
        }

        value = [];
        cas = 0;
        return WasmResult.NotFound;
    }

    /// <summary>
    /// <c>proxy_set_shared_data</c>. A non-zero <paramref name="cas"/> must equal the current one, or the write fails with
    /// CasMismatch and changes nothing. Writes past the scope's byte limit fail with InternalFailure.
    /// </summary>
    public int Set(string key, byte[] value, uint cas)
    {
        lock (_gate)
        {
            var exists = _data.TryGetValue(key, out var current);
            if (cas != 0 && (!exists || current.Cas != cas))
            {
                return WasmResult.CasMismatch;
            }

            var size = _dataBytes - (exists ? key.Length + current.Value.Length : 0) + key.Length + value.Length;
            if (size > _dataLimit)
            {
                return WasmResult.InternalFailure;
            }

            _nextCas = _nextCas == uint.MaxValue ? 1 : _nextCas + 1;
            _data[key] = (value, _nextCas);
            _dataBytes = size;
            return WasmResult.Ok;
        }
    }

    /// <summary>Registers (or re-registers) a queue in this scope and returns its token. The caller becomes a reader.</summary>
    public int RegisterQueue(string name, WasmVm vm, int rootId)
    {
        WasmSharedQueue queue;
        lock (Registry)
        {
            lock (_gate)
            {
                if (!_queues.TryGetValue(name, out queue!))
                {
                    queue = new WasmSharedQueue(++_nextToken, this);
                    _queues[name] = queue;
                    QueuesByToken[queue.Token] = queue;
                }
            }
        }

        queue.AddReader(vm, rootId);
        return queue.Token;
    }

    /// <summary><c>proxy_resolve_shared_queue</c>: the token of queue <paramref name="name"/> in scope <paramref name="vmId"/>.</summary>
    public static bool TryResolveQueue(string vmId, string name, out int token)
    {
        lock (Registry)
        {
            if (Scopes.TryGetValue(vmId, out var scope))
            {
                lock (scope._gate)
                {
                    if (scope._queues.TryGetValue(name, out var queue))
                    {
                        token = queue.Token;
                        return true;
                    }
                }
            }
        }

        token = 0;
        return false;
    }

    public static WasmSharedQueue? FindQueue(int token)
    {
        lock (Registry)
        {
            return QueuesByToken.GetValueOrDefault(token);
        }
    }
}

/// <summary>
/// A shared queue. Any VM may enqueue; the root contexts that registered it are its readers, and each enqueue tells one
/// of them (round robin) through <c>proxy_on_queue_ready</c>, delivered off the enqueuing call.
/// </summary>
internal sealed class WasmSharedQueue(int token, WasmSharedState scope)
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _items = new();
    private readonly List<(WeakReference<WasmVm> Vm, int RootId)> _readers = [];
    private long _bytes;
    private int _next;

    public int Token { get; } = token;

    public WasmSharedState Scope { get; } = scope;

    public void AddReader(WasmVm vm, int rootId)
    {
        lock (_gate)
        {
            _readers.RemoveAll(r => !r.Vm.TryGetTarget(out var v) || v.IsDisposed || v.Faulted || (ReferenceEquals(v, vm) && r.RootId == rootId));
            _readers.Add((new WeakReference<WasmVm>(vm), rootId));
        }
    }

    /// <summary>Adds an item. Returns InternalFailure when the queue is full; otherwise notifies one reader.</summary>
    public int Enqueue(byte[] item)
    {
        (WasmVm Vm, int RootId)? reader = null;
        lock (_gate)
        {
            if (_bytes + item.Length > Scope.QueueLimit)
            {
                return WasmResult.InternalFailure;
            }

            _items.Enqueue(item);
            _bytes += item.Length;
            for (var i = 0; i < _readers.Count && reader is null; i++)
            {
                var candidate = _readers[(int)((uint)_next++ % (uint)_readers.Count)];
                if (candidate.Vm.TryGetTarget(out var vm) && vm is { IsDisposed: false, Faulted: false })
                {
                    reader = (vm, candidate.RootId);
                }
            }
        }

        if (reader is { } target)
        {
            var token = Token;
            _ = Task.Run(() => target.Vm.Pool.Deliver(target.Vm, target.RootId, _ => target.Vm.Call("proxy_on_queue_ready", [target.RootId, token])));
        }

        return WasmResult.Ok;
    }

    public int Dequeue(out byte[] item)
    {
        lock (_gate)
        {
            if (_items.TryDequeue(out item!))
            {
                _bytes -= item.Length;
                return WasmResult.Ok;
            }
        }

        item = [];
        return WasmResult.Empty;
    }
}
