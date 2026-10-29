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

    public WasmVmPool(string name, WasmModule module, WasmLimits limits, ILogger? logger)
    {
        Name = name;
        Module = module;
        Limits = limits;
        Logger = logger;
        _slots = new WasmVm?[limits.PoolSize];
        _rebuilding = new bool[limits.PoolSize];
    }

    public string Name { get; }

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

    public int NextContextId() => Interlocked.Increment(ref _nextContextId);

    /// <summary>Builds every VM and starts the root context of each attached plugin in it. Throws, keeping nothing, on failure.</summary>
    public void Start()
    {
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            _slots[slot] = Build(slot);
        }
    }

    /// <summary>Adds a plugin, giving it a root context id. VMs that already exist start its root context now.</summary>
    public void Attach(WasmPlugin plugin)
    {
        WasmVm?[] existing;
        lock (_gate)
        {
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
            return _plugins.Count == 0;
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

    /// <summary>S6a records the period; the tick timer arrives with the callout work.</summary>
    public int SetTickPeriod(WasmVm vm, int milliseconds)
    {
        if (vm.Effective is not WasmRootContext root)
        {
            return WasmResult.BadArgument;
        }

        root.TickPeriodMs = Math.Max(0, milliseconds);
        return WasmResult.Ok;
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

        Module.Dispose();
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

        plugin.OnRootStarted(root);
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
