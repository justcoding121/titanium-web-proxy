using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The VMs that serve one module: <see cref="WasmLimits.PoolSize"/> stores, each with its own instance and linear
/// memory. A request takes one VM (round robin over healthy ones) and keeps it for life. A VM that traps leaves its slot
/// and a fresh one is built off the request path, with every attached plugin's root context started again.
/// </summary>
internal sealed class WasmVmPool : IDisposable
{
    private readonly object _gate = new();
    private readonly WasmVm?[] _slots;
    private readonly List<WasmPlugin> _plugins = [];
    private readonly bool[] _rebuilding;
    private readonly List<Task> _rebuilds = [];
    private int _next;
    private int _nextContextId;
    private bool _disposed;
    private bool _closing;

    public WasmVmPool(string name, WasmModule module, WasmLimits limits, ILogger? logger, string? vmId = null, string? sharingKey = null)
    {
        Name = name;
        Module = module;
        Limits = limits;
        Logger = logger;
        VmId = vmId;
        SharingKey = sharingKey;
        _slots = new WasmVm?[limits.PoolSize];
        _rebuilding = new bool[limits.PoolSize];
        Shared = WasmSharedState.Acquire(vmId ?? string.Empty, limits);
        Callouts = new WasmCallouts(this);
    }

    public string Name { get; }

    /// <summary>The <c>vm_id</c> plugins share this pool by, or null for a pool of its own.</summary>
    public string? VmId { get; }

    /// <summary>Key in <see cref="WasmVmPools"/> when the pool is shared by <c>vm_id</c>.</summary>
    public string? SharingKey { get; }

    /// <summary>Shared data and queues of this pool's <c>vm_id</c>.</summary>
    public WasmSharedState Shared { get; }

    public WasmCallouts Callouts { get; }

    public WasmModule Module { get; }

    public WasmLimits Limits { get; }

    public ILogger? Logger { get; }

    public ProxyLogLevel LogLevel =>
        Logger is null ? ProxyLogLevel.Critical
        : Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) ? ProxyLogLevel.Trace
        : Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) ? ProxyLogLevel.Debug
        : Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information) ? ProxyLogLevel.Info
        : Logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning) ? ProxyLogLevel.Warn
        : ProxyLogLevel.Error;

    /// <summary>Number of slots holding a healthy VM.</summary>
    public int HealthyCount
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count(v => v is { Faulted: false, IsDisposed: false });
            }
        }
    }

    /// <summary>The VMs in their slots right now; an empty slot (a rebuild in progress) is null.</summary>
    public WasmVm?[] Vms
    {
        get
        {
            lock (_gate)
            {
                return _slots.ToArray();
            }
        }
    }

    public int NextContextId() => Interlocked.Increment(ref _nextContextId);

    /// <summary>Builds every VM and starts the root context of each attached plugin in it. Throws, keeping nothing, on failure.</summary>
    public void Start()
    {
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            _slots[slot] = Build(slot);
        }
    }

    /// <summary>
    /// Adds a plugin, giving it a root context id. VMs that already exist start its root context now. Throws
    /// <see cref="WasmPoolClosedException"/> when the pool's last plugin already left, so the caller builds a new pool.
    /// </summary>
    public void Attach(WasmPlugin plugin)
    {
        WasmVm?[] existing;
        lock (_gate)
        {
            if (_closing || _disposed)
            {
                throw new WasmPoolClosedException();
            }

            plugin.RootId = NextContextId();
            _plugins.Add(plugin);
            existing = _slots.ToArray();
        }

        foreach (var vm in existing)
        {
            if (vm is null)
            {
                continue;
            }

            lock (vm.Gate)
            {
                StartRoot(vm, plugin);
            }
        }
    }

    /// <summary>Ends a plugin's root context in every VM. Returns true when no plugin is left, so the caller disposes the pool.</summary>
    public bool Detach(WasmPlugin plugin)
    {
        WasmVm?[] vms;
        lock (_gate)
        {
            _plugins.Remove(plugin);
            vms = _slots.ToArray();
        }

        foreach (var vm in vms)
        {
            if (vm is null)
            {
                continue;
            }

            lock (vm.Gate)
            {
                if (vm.IsDisposed || !vm.TryGetContext(plugin.RootId, out var root))
                {
                    continue;
                }

                try
                {
                    if (!vm.Faulted)
                    {
                        vm.Effective = root;
                        vm.Call("proxy_on_done", [plugin.RootId], 1);
                        vm.Call("proxy_on_delete", [plugin.RootId]);
                    }
                }
                catch (WasmVmFaultException)
                {
                    // A guest that traps while it shuts down has nothing more to say.
                }
                finally
                {
                    root.OnVmDisposed();
                    vm.Unregister(plugin.RootId);
                }
            }
        }

        lock (_gate)
        {
            if (_plugins.Count == 0)
            {
                _closing = true;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> in <paramref name="vm"/> with context <paramref name="contextId"/> effective, for
    /// work that arrives off the request path (callout responses, ticks, queue notifications). Does nothing when the VM
    /// faulted or went away, or the context ended. A trap in the callback discards the VM as any trap does.
    /// </summary>
    public bool Deliver(WasmVm vm, int contextId, Action<WasmContext> action)
    {
        lock (vm.Gate)
        {
            if (vm.Faulted || vm.IsDisposed || !vm.TryGetContext(contextId, out var context))
            {
                return false;
            }

            vm.Effective = context;
            try
            {
                action(context);
                return true;
            }
            catch (WasmVmFaultException ex)
            {
                Log(ProxyLogLevel.Warn, $"callback for context {contextId} failed: {ex.Message}");
                return false;
            }
            finally
            {
                vm.Effective = null;
            }
        }
    }

    /// <summary>Picks a healthy VM for a new request. Throws <see cref="WasmVmFaultException"/> when none is healthy.</summary>
    public WasmVm Acquire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new WasmVmFaultException("the plugin was unloaded");
            }

            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = (int)((uint)Interlocked.Increment(ref _next) % (uint)_slots.Length);
                if (_slots[slot] is { Faulted: false, IsDisposed: false } vm)
                {
                    return vm;
                }
            }
        }

        throw new WasmVmFaultException("no healthy VM is available");
    }

    /// <summary>Called by a VM that just faulted: discard it and rebuild the slot on the thread pool.</summary>
    public void OnFaulted(WasmVm vm)
    {
        lock (_gate)
        {
            if (_disposed || _rebuilding[vm.Slot])
            {
                return;
            }

            _rebuilding[vm.Slot] = true;
            _rebuilds.Add(Task.Run(() => Rebuild(vm)));
            _rebuilds.RemoveAll(static t => t.IsCompleted);
        }
    }

    public void Log(ProxyLogLevel level, string message) =>
        Logger?.Log(WasmImports.ToLogLevel(level), "[wasm {Plugin}] {Message}", Name, message);

    /// <summary>
    /// <c>proxy_set_tick_period_milliseconds</c> from a root context: (re)starts that root's timer in this VM, or stops it
    /// for zero. Each tick calls <c>proxy_on_tick</c> with the VM's gate held; a tick that finds the previous one still
    /// running is skipped.
    /// </summary>
    public int SetTickPeriod(WasmVm vm, int milliseconds)
    {
        if (vm.Effective is not { } effective || !vm.TryGetContext(effective.RootId, out var context) || context is not WasmRootContext root)
        {
            return WasmResult.BadArgument;
        }

        root.TickPeriodMs = Math.Max(0, milliseconds);
        root.Ticker?.Dispose();
        root.Ticker = null;
        if (root.TickPeriodMs > 0)
        {
            var period = TimeSpan.FromMilliseconds(root.TickPeriodMs);
            root.Ticker = new Timer(static state => Tick((WasmRootContext)state!), root, period, period);
        }

        return WasmResult.Ok;
    }

    private static void Tick(WasmRootContext root)
    {
        if (Interlocked.Exchange(ref root.Ticking, 1) == 1)
        {
            return;
        }

        try
        {
            root.Vm.Pool.Deliver(root.Vm, root.Id, _ => root.Vm.Call("proxy_on_tick", [root.Id]));
        }
        finally
        {
            Volatile.Write(ref root.Ticking, 0);
        }
    }

    public void Dispose()
    {
        WasmVm?[] vms;
        Task[] rebuilds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            vms = _slots.ToArray();
            Array.Clear(_slots);
            rebuilds = _rebuilds.ToArray();
        }

        if (SharingKey is not null)
        {
            WasmVmPools.Remove(SharingKey, this);
        }

        Callouts.Dispose();

        // A rebuild instantiates from the module, so the module must outlive it.
        Task.WaitAll(rebuilds);

        foreach (var vm in vms)
        {
            if (vm is null)
            {
                continue;
            }

            lock (vm.Gate)
            {
                vm.Dispose();
            }
        }

        Shared.Release();
        Module.Dispose();
    }

    /// <summary>True when the pool can take another plugin (it still has one and is not shutting down).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_closing && !_disposed;
            }
        }
    }

    private WasmVm Build(int slot)
    {
        var vm = WasmVm.Create(this, slot);
        try
        {
            WasmPlugin[] plugins;
            lock (_gate)
            {
                plugins = _plugins.ToArray();
            }

            lock (vm.Gate)
            {
                foreach (var plugin in plugins)
                {
                    StartRoot(vm, plugin);
                }

                vm.Ready = true;
            }

            return vm;
        }
        catch
        {
            lock (vm.Gate)
            {
                vm.Dispose();
            }

            throw;
        }
    }

    private static void StartRoot(WasmVm vm, WasmPlugin plugin)
    {
        var root = new WasmRootContext(vm, plugin.RootId, plugin);
        vm.Register(root);
        vm.Effective = root;
        vm.Call("proxy_on_context_create", [root.Id, 0]);
        if (vm.Call("proxy_on_vm_start", [root.Id, plugin.VmConfiguration.Length], 1) == 0)
        {
            throw new InvalidOperationException("proxy_on_vm_start returned false");
        }

        if (vm.Call("proxy_on_configure", [root.Id, plugin.Configuration.Length], 1) == 0)
        {
            throw new InvalidOperationException("proxy_on_configure rejected the plugin configuration");
        }
    }

    private void Rebuild(WasmVm faulted)
    {
        lock (faulted.Gate)
        {
            faulted.Dispose();
        }

        WasmVm? fresh = null;
        try
        {
            bool disposed;
            lock (_gate)
            {
                disposed = _disposed;
            }

            if (!disposed)
            {
                fresh = Build(faulted.Slot);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "WASM plugin '{Plugin}' could not rebuild VM {Slot}; the slot stays empty.", Name, faulted.Slot);
        }

        lock (_gate)
        {
            _rebuilding[faulted.Slot] = false;
            if (_disposed)
            {
                if (fresh is not null)
                {
                    lock (fresh.Gate)
                    {
                        fresh.Dispose();
                    }
                }

                return;
            }

            if (ReferenceEquals(_slots[faulted.Slot], faulted))
            {
                _slots[faulted.Slot] = fresh;
            }
        }
    }
}

/// <summary>The pool's last plugin left while another tried to join it.</summary>
internal sealed class WasmPoolClosedException() : Exception("the shared WASM VM pool is shutting down");

/// <summary>
/// Pools shared by <c>vm_id</c>. Plugins with the same <c>vm_id</c>, module bytes, engine and limits run in the same VMs,
/// each with its own root context, as Envoy shares a VM between filters. Anything else gets a pool of its own.
/// </summary>
internal static class WasmVmPools
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, WasmVmPool> Pools = new(StringComparer.Ordinal);

    public static string Key(string vmId, string moduleHash, WasmRuntime runtime, WasmLimits limits) =>
        string.Join('\n', vmId, moduleHash, runtime.Fingerprint, limits.ToString());

    public static WasmVmPool? Find(string key)
    {
        lock (Gate)
        {
            return Pools.TryGetValue(key, out var pool) && pool.IsOpen ? pool : null;
        }
    }

    public static void Add(string key, WasmVmPool pool)
    {
        lock (Gate)
        {
            Pools[key] = pool;
        }
    }

    public static void Remove(string key, WasmVmPool pool)
    {
        lock (Gate)
        {
            if (Pools.TryGetValue(key, out var current) && ReferenceEquals(current, pool))
            {
                Pools.Remove(key);
            }
        }
    }
}
