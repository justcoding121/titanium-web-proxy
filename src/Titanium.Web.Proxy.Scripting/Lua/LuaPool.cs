using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// One Lua state with the script loaded. <see cref="Gate"/> is held only while native code runs, never across a callout,
/// so other requests use the state while a call waits. A call counts as in flight from start to finish; a retired state
/// is destroyed when its last call finishes, on a pool thread.
/// </summary>
internal sealed class LuaInstance
{
    private int _inFlight;
    private int _retired;
    private int _destroyed;

    public LuaInstance(LuaStateHandle handle, IReadOnlySet<string> functions)
    {
        Handle = handle;
        Functions = functions;
    }

    public LuaStateHandle Handle { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The <c>on_*</c> entry points the script defines.</summary>
    public IReadOnlySet<string> Functions { get; }

    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Registers a call. Fails once the state is retired.</summary>
    public bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (!IsRetired)
        {
            return true;
        }

        Exit();
        return false;
    }

    public void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && IsRetired)
        {
            Destroy();
        }
    }

    /// <summary>Stops new calls. The state is destroyed now if idle, otherwise when its last call finishes.</summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0 && InFlight == 0)
        {
            Destroy();
        }
    }

    private void Destroy()
    {
        if (Interlocked.Exchange(ref _destroyed, 1) != 0)
        {
            return;
        }

        // Closing a large heap runs finalizers and frees every object; keep that off the request thread. The gate is not
        // disposed: a caller may still be waiting on it, and it gives up once it sees the state retired.
        ThreadPool.UnsafeQueueUserWorkItem(static instance => instance.Handle.Dispose(), this, preferLocal: false);
    }
}

/// <summary>
/// Fixed-size pool of <see cref="LuaInstance"/>. Every state is built before the pool is returned. A state that tripped a
/// limit leaves its slot at once and a replacement is built in the background.
/// </summary>
internal sealed class LuaPool : IDisposable
{
    private readonly LuaInstance?[] _slots;
    private readonly Func<LuaInstance> _factory;
    private readonly ILogger? _logger;
    private readonly string _name;
    private readonly CancellationTokenSource _disposed = new();
    private int _next;
    private int _rebuilding;

    private LuaPool(string name, LuaInstance?[] slots, Func<LuaInstance> factory, ILogger? logger)
    {
        _name = name;
        _slots = slots;
        _factory = factory;
        _logger = logger;
    }

    public int Size => _slots.Length;

    /// <summary>States being rebuilt after a limit trip.</summary>
    public int Rebuilding => Volatile.Read(ref _rebuilding);

    public IEnumerable<LuaInstance> Live => _slots.Select(s => Volatile.Read(ref s)).OfType<LuaInstance>();

    /// <summary>Builds every state, so a script that fails to load fails here, before anything is swapped in.</summary>
    public static LuaPool Create(string name, int size, Func<LuaInstance> factory, ILogger? logger)
    {
        var slots = new LuaInstance?[size];
        try
        {
            for (var i = 0; i < size; i++)
            {
                slots[i] = factory();
            }
        }
        catch
        {
            foreach (var slot in slots)
            {
                slot?.Retire();
            }

            throw;
        }

        return new LuaPool(name, slots, factory, logger);
    }

    /// <summary>Takes a state's gate and registers a call on it. The caller releases the gate and calls <see cref="LuaInstance.Exit"/>.</summary>
    public async ValueTask<LuaInstance> AcquireAsync(CancellationToken cancellationToken) // NOSONAR S3776 -- Round-robin slot scan and fallback wait share one gate lifecycle; splitting would obscure acquire ordering.
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            var start = (int)((uint)Interlocked.Increment(ref _next) % (uint)_slots.Length);
            LuaInstance? fallback = null;
            for (var i = 0; i < _slots.Length; i++)
            {
                var instance = Volatile.Read(ref _slots[(start + i) % _slots.Length]);
                if (instance is null)
                {
                    continue;
                }

                fallback ??= instance;
                if (await instance.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    if (instance.TryEnter())
                    {
                        return instance;
                    }

                    instance.Gate.Release();
                }
            }

            if (fallback is null)
            {
                // Every state is being rebuilt.
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await fallback.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (fallback.TryEnter())
            {
                return fallback;
            }

            fallback.Gate.Release();
        }
    }

    /// <summary>Takes <paramref name="instance"/> out of service and starts building its replacement.</summary>
    public void Discard(LuaInstance instance)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (Interlocked.CompareExchange(ref _slots[i], null, instance) == instance)
            {
                instance.Retire();
                Interlocked.Increment(ref _rebuilding);
                var slot = i;
                _ = Task.Run(() => RebuildAsync(slot), _disposed.Token);
                return;
            }
        }

        instance.Retire();
    }

    private async Task RebuildAsync(int slot)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        try
        {
            while (!_disposed.IsCancellationRequested)
            {
                try
                {
                    var fresh = _factory();
                    if (_disposed.IsCancellationRequested ||
                        Interlocked.CompareExchange(ref _slots[slot], fresh, null) is not null)
                    {
                        fresh.Retire();
                    }

                    return;
                }
                catch (Exception ex) when (ex is LuaScriptException or InvalidOperationException)
                {
                    _logger?.LogWarning(ex, "Lua plugin '{Plugin}' could not rebuild a state: {Error}", _name, ex.Message);
                }

                await Task.Delay(delay, _disposed.Token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 10_000));
            }
        }
        catch (OperationCanceledException)
        {
            /* pool disposed while rebuilding */
        }
        finally
        {
            Interlocked.Decrement(ref _rebuilding);
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested)
        {
            return;
        }

        _disposed.Cancel();
        for (var i = 0; i < _slots.Length; i++)
        {
            Interlocked.Exchange(ref _slots[i], null)?.Retire();
        }

        _disposed.Dispose();
    }
}
