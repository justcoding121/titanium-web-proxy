using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>A VM trapped, ran out of budget, or was discarded. The request it served fails by the plugin's failMode.</summary>
internal sealed class WasmVmFaultException(string message) : Exception(message);

/// <summary>
/// One Wasmtime store with one instance of the module, plus the proxy-wasm contexts that live in it. Every entry into the
/// guest happens with <see cref="Gate"/> held, and the lock is held only for that call, never across an await. A trap
/// marks the VM <see cref="Faulted"/>; it is never called again and its pool slot gets a fresh VM.
/// </summary>
internal sealed unsafe class WasmVm : IDisposable
{
    private readonly Dictionary<string, (WasmtimeFunc Func, WasmExportShape Shape)> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, WasmContext> _contexts = [];
    private readonly StoreHandle _store;
    private readonly IntPtr _context;
    private readonly WasmtimeInstance _instance;
    private readonly WasmtimeMemory _memory;
    private readonly bool _hasMemory;
    private readonly string? _allocator;
    private GCHandle _self;
    private int _depth;
    private int? _exitCode;

    private WasmVm(WasmVmPool pool, int slot)
    {
        Pool = pool;
        Slot = slot;
        Module = pool.Module;
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _store = new StoreHandle(Wasmtime.wasmtime_store_new(Module.Runtime.Engine.DangerousGetHandle(), GCHandle.ToIntPtr(_self), IntPtr.Zero));
        _context = Wasmtime.wasmtime_store_context(_store.DangerousGetHandle());

        // Bound what the guest can allocate: the configured memory ceiling per linear memory and a table cap.
        Wasmtime.wasmtime_store_limiter(_store.DangerousGetHandle(), pool.Limits.MemoryBytes, 100_000, -1, -1, -1);
        ArmBudget();

        WasmtimeInstance instance;
        IntPtr trap;
        var error = Wasmtime.wasmtime_instance_pre_instantiate(Module.InstancePre.DangerousGetHandle(), _context, &instance, &trap);
        if (error != IntPtr.Zero)
        {
            throw new InvalidOperationException("the module does not instantiate: " + Wasmtime.TakeError(error));
        }

        if (trap != IntPtr.Zero)
        {
            throw new InvalidOperationException("the module trapped while it instantiated: " + Wasmtime.TakeTrap(trap, out _));
        }

        _instance = instance;
        WasmtimeExtern memory;
        if (TryGetExport("memory", &memory) && memory.Kind == Wasmtime.ExternKindMemory)
        {
            _memory = memory.Memory;
            _hasMemory = true;
        }

        _allocator = Module.HasExport("proxy_on_memory_allocate") ? "proxy_on_memory_allocate"
            : Module.HasExport("malloc") ? "malloc"
            : null;
    }

    public object Gate { get; } = new();

    public WasmVmPool Pool { get; }

    public WasmModule Module { get; }

    public int Slot { get; }

    public bool Faulted { get; private set; }

    public string? FaultReason { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>The context host functions act on: the one being called, or the one <c>proxy_set_effective_context</c> chose.</summary>
    public WasmContext? Effective { get; set; }

    public ILogger? Logger => Pool.Logger;

    /// <summary>Instantiates the module and runs its WASI start function, if any.</summary>
    public static WasmVm Create(WasmVmPool pool, int slot)
    {
        var vm = new WasmVm(pool, slot);
        try
        {
            lock (vm.Gate)
            {
                vm.RunStart();
            }

            return vm;
        }
        catch
        {
            vm.Dispose();
            throw;
        }
    }

    public bool HasExport(string name) => Module.HasExport(name);

    public void Register(WasmContext context) => _contexts[context.Id] = context;

    public void Unregister(int id) => _contexts.Remove(id);

    public bool TryGetContext(int id, out WasmContext context) => _contexts.TryGetValue(id, out context!);

    public IEnumerable<WasmContext> Contexts => _contexts.Values;

    /// <summary>The header maps of the callout response being delivered, for the <c>HttpCall*</c> and gRPC map types.</summary>
    public WasmHeaderMap? GetCalloutMap(ProxyMapType type) => null;

    /// <summary>
    /// Calls an export with <see cref="Gate"/> held. A missing export returns <paramref name="missing"/>. A trap or an
    /// exhausted budget at the outermost call marks the VM faulted and throws <see cref="WasmVmFaultException"/>; inside a
    /// host function (a nested call such as <c>malloc</c>) it throws <see cref="WasmTrapException"/> so the outer call traps.
    /// </summary>
    public int Call(string export, ReadOnlySpan<int> args, int missing = 0)
    {
        if (Faulted || IsDisposed)
        {
            throw new WasmVmFaultException(FaultReason ?? "the VM was discarded");
        }

        if (!TryGetFunction(export, out var func, out var shape))
        {
            return missing;
        }

        var length = Math.Max(Math.Max(shape.Parameters.Length, shape.Results), 1);
        var buffer = stackalloc WasmValRaw[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = default;
        }

        for (var i = 0; i < shape.Parameters.Length && i < args.Length; i++)
        {
            if (shape.Parameters[i] == 'I')
            {
                buffer[i].I64 = args[i];
            }
            else
            {
                buffer[i].I32 = args[i];
            }
        }

        var outermost = _depth == 0;
        if (outermost)
        {
            ArmBudget();
        }

        IntPtr error;
        IntPtr trap;
        _depth++;
        try
        {
            error = Wasmtime.wasmtime_func_call_unchecked(_context, &func, buffer, (nuint)length, &trap);
        }
        finally
        {
            _depth--;
        }

        string? failure = null;
        if (error != IntPtr.Zero)
        {
            failure = Wasmtime.TakeError(error);
        }
        else if (trap != IntPtr.Zero)
        {
            var message = Wasmtime.TakeTrap(trap, out var code);
            failure = code switch
            {
                Wasmtime.TrapCodeInterrupt => $"{export} ran past its epoch deadline ({Pool.Limits.EpochDeadlineMs} ms)",
                Wasmtime.TrapCodeOutOfFuel => $"{export} ran out of fuel ({Pool.Limits.Fuel})",
                _ => $"{export} trapped: {message}",
            };
        }

        if (failure is not null)
        {
            if (!outermost)
            {
                throw new WasmTrapException(failure);
            }

            MarkFaulted(failure);
            throw new WasmVmFaultException(failure);
        }

        return shape.Results > 0 ? buffer[0].I32 : missing;
    }

    /// <summary>Records that <c>proc_exit</c> ran, so a WASI command that exits 0 from <c>_start</c> still counts as started.</summary>
    public void RecordExit(int code) => _exitCode = code;

    public void MarkFaulted(string reason)
    {
        if (Faulted)
        {
            return;
        }

        Faulted = true;
        FaultReason = reason;
        if (Ready)
        {
            Logger?.LogWarning("WASM plugin VM {Slot} of '{Plugin}' was discarded: {Reason}", Slot, Pool.Name, reason);
            Pool.OnFaulted(this);
        }
    }

    /// <summary>Set once the VM is in its pool slot. A fault before that fails the load instead of replacing the VM.</summary>
    public bool Ready { get; set; }

    public void ReportHostError(string function, Exception ex) =>
        Logger?.LogDebug(ex, "WASM host function {Function} failed for '{Plugin}'.", function, Pool.Name);

    // Linear memory. Every pointer and length is bounds-checked; a bad one throws WasmMemoryException, which the
    // trampoline turns into InvalidMemoryAccess. Pointers are re-read after any call into the guest, which may grow memory.

    public Span<byte> Memory(int pointer, int length)
    {
        if (!_hasMemory)
        {
            throw new WasmMemoryException("the module exports no memory");
        }

        var memory = _memory;
        var data = Wasmtime.wasmtime_memory_data(_context, &memory);
        var size = (ulong)Wasmtime.wasmtime_memory_data_size(_context, &memory);
        var start = (uint)pointer;
        var count = (uint)length;
        if (length < 0 || (ulong)start + count > size)
        {
            throw new WasmMemoryException($"guest range {start}+{count} is outside linear memory ({size} bytes)");
        }

        return new Span<byte>(data + start, (int)count);
    }

    public byte[] ReadBytes(int pointer, int length) => length == 0 ? [] : Memory(pointer, length).ToArray();

    public string ReadString(int pointer, int length) => length == 0 ? string.Empty : Encoding.UTF8.GetString(Memory(pointer, length));

    public void WriteU32(int pointer, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Memory(pointer, 4), value);

    public void WriteI32(int pointer, int value) => BinaryPrimitives.WriteInt32LittleEndian(Memory(pointer, 4), value);

    public void WriteU64(int pointer, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Memory(pointer, 8), value);

    public uint ReadU32(int pointer) => BinaryPrimitives.ReadUInt32LittleEndian(Memory(pointer, 4));

    /// <summary>Allocates guest memory through the module's allocator export.</summary>
    public int Allocate(int size)
    {
        if (_allocator is null)
        {
            throw new WasmMemoryException("the module exports neither proxy_on_memory_allocate nor malloc");
        }

        var pointer = Call(_allocator, [size]);
        if (pointer == 0)
        {
            throw new WasmMemoryException($"the guest allocator returned null for {size} bytes");
        }

        return pointer;
    }

    /// <summary>
    /// The proxy-wasm way of returning bytes: allocate in the guest, copy, and write the pointer and size to the two
    /// return slots. An empty value is returned as a null pointer and zero size, as other hosts do.
    /// </summary>
    public void ReturnBytes(ReadOnlySpan<byte> data, int pointerSlot, int sizeSlot)
    {
        // Validate the slots before allocating, so a bad slot does not leak guest memory.
        Memory(pointerSlot, 4);
        Memory(sizeSlot, 4);
        var pointer = 0;
        if (data.Length > 0)
        {
            pointer = Allocate(data.Length);
            data.CopyTo(Memory(pointer, data.Length));
        }

        WriteI32(pointerSlot, pointer);
        WriteI32(sizeSlot, data.Length);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        foreach (var context in _contexts.Values)
        {
            context.OnVmDisposed();
        }

        _contexts.Clear();
        _store.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void RunStart()
    {
        // A reactor (_initialize) or a command (_start). A command that calls proc_exit(0) has finished normally.
        var start = Module.HasExport("_initialize") ? "_initialize" : Module.HasExport("_start") ? "_start" : null;
        if (start is null)
        {
            return;
        }

        try
        {
            Call(start, []);
        }
        catch (WasmVmFaultException) when (_exitCode == 0)
        {
            Faulted = false;
            FaultReason = null;
        }
    }

    private void ArmBudget()
    {
        var limits = Pool.Limits;
        Wasmtime.wasmtime_context_set_epoch_deadline(_context, (ulong)Math.Max(1, (limits.EpochDeadlineMs + WasmRuntime.EpochTickMilliseconds - 1) / WasmRuntime.EpochTickMilliseconds));
        if (Module.Runtime.ConsumesFuel && limits.Fuel is long fuel)
        {
            var error = Wasmtime.wasmtime_context_set_fuel(_context, (ulong)fuel);
            if (error != IntPtr.Zero)
            {
                throw new InvalidOperationException(Wasmtime.TakeError(error));
            }
        }
    }

    private bool TryGetFunction(string name, out WasmtimeFunc func, out WasmExportShape shape)
    {
        if (_functions.TryGetValue(name, out var cached))
        {
            func = cached.Func;
            shape = cached.Shape;
            return true;
        }

        func = default;
        shape = default;
        if (!Module.Exports.TryGetValue(name, out shape))
        {
            return false;
        }

        WasmtimeExtern item;
        if (!TryGetExport(name, &item) || item.Kind != Wasmtime.ExternKindFunc)
        {
            return false;
        }

        func = item.Func;
        _functions[name] = (func, shape);
        return true;
    }

    private bool TryGetExport(string name, WasmtimeExtern* item)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var instance = _instance;
        fixed (byte* p = bytes)
        {
            return Wasmtime.wasmtime_instance_export_get(_context, &instance, p, (nuint)bytes.Length, item) != 0;
        }
    }
}
