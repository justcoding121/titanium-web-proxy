using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The process-wide Wasmtime engine and linker. Created the first time a <c>kind: wasm</c> plugin loads, so a
/// configuration with no WASM plugin never loads the native library. There are two engines: one that meters fuel and one
/// that does not, because fuel is an engine setting and costs every call even when unused.
/// </summary>
/// <remarks>
/// Engine settings: epoch interruption is on (a call that overruns its deadline traps), signal-based traps are off so
/// Wasmtime installs no SIGSEGV/SIGILL handlers that would fight the .NET runtime (bounds checks are explicit instead),
/// and each linear memory reserves at most <see cref="MemoryReservationBytes"/> of address space and moves when it grows.
/// </remarks>
internal sealed class WasmRuntime
{
    /// <summary>Wall time of one epoch tick. A call's deadline is its budget divided by this.</summary>
    public const int EpochTickMilliseconds = 2;

    internal const ulong MemoryReservationBytes = 64UL * 1024 * 1024;
    internal const ulong MemoryReservationForGrowthBytes = 16UL * 1024 * 1024;
    internal const nuint MaxWasmStackBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static WasmRuntime? _plain;
    private static WasmRuntime? _fueled;
    private static Thread? _ticker;
    private static readonly List<WasmRuntime> Running = [];

    private readonly List<GCHandle> _pinned = [];

    private WasmRuntime(bool consumeFuel)
    {
        ConsumesFuel = consumeFuel;
        Engine = CreateEngine(consumeFuel);
        Fingerprint = $"wasmtime-{Wasmtime.Version};{RuntimeInformation.ProcessArchitecture};epoch;nosignals;" +
                      $"res={MemoryReservationBytes};grow={MemoryReservationForGrowthBytes};stack={MaxWasmStackBytes};fuel={consumeFuel}";
        Functions = WasmImports.All.ToDictionary(f => f.Key, StringComparer.Ordinal);
        Linker = CreateLinker();
    }

    public bool ConsumesFuel { get; }

    public EngineHandle Engine { get; }

    public LinkerHandle Linker { get; }

    /// <summary>Everything about the engine that a serialized module depends on. Part of the module cache key.</summary>
    public string Fingerprint { get; }

    public IReadOnlyDictionary<string, WasmHostFunction> Functions { get; }

    /// <summary>The current epoch count the ticker has added, for tests and diagnostics.</summary>
    public static long EpochTicks => Interlocked.Read(ref _epochTicks);

    private static long _epochTicks;

    /// <summary>Returns the engine for the fuel setting, creating it (and the shared epoch ticker) on first use.</summary>
    public static WasmRuntime Get(bool consumeFuel)
    {
        lock (Gate)
        {
            ref var slot = ref consumeFuel ? ref _fueled : ref _plain;
            if (slot is null)
            {
                slot = new WasmRuntime(consumeFuel);
                Running.Add(slot);
                StartTicker();
            }

            return slot;
        }
    }

    /// <summary>True when the Wasmtime native library loads. Sets <paramref name="error"/> to a fix the operator can act on.</summary>
    public static bool TryLoadNative(out string? error)
    {
        if (NativeLibrary.TryLoad(Wasmtime.Library, typeof(WasmRuntime).Assembly, null, out _))
        {
            error = null;
            return true;
        }

        var file = OperatingSystem.IsWindows() ? "wasmtime.dll" : OperatingSystem.IsMacOS() ? "libwasmtime.dylib" : "libwasmtime.so";
        error = $"the Wasmtime native library ({file}) was not found next to the application or on the library path; " +
                "run 'titanium scripting-deps status' for fixes";
        return false;
    }

    /// <summary>Compiles WebAssembly text to binary. Used by tests and by <c>.wat</c> plugin files.</summary>
    public static unsafe byte[] WatToWasm(string wat)
    {
        var text = Encoding.UTF8.GetBytes(wat);
        WasmByteVec result = default;
        IntPtr error;
        fixed (byte* p = text)
        {
            error = Wasmtime.wasmtime_wat2wasm(p, (nuint)text.Length, &result);
        }

        if (error != IntPtr.Zero)
        {
            throw new InvalidOperationException("invalid WebAssembly text: " + Wasmtime.TakeError(error));
        }

        try
        {
            return new ReadOnlySpan<byte>(result.Data, checked((int)result.Size)).ToArray();
        }
        finally
        {
            Wasmtime.wasm_byte_vec_delete(&result);
        }
    }

    private static EngineHandle CreateEngine(bool consumeFuel)
    {
        var config = Wasmtime.wasm_config_new();
        if (config == IntPtr.Zero)
        {
            throw new InvalidOperationException("Wasmtime could not allocate an engine configuration.");
        }

        Wasmtime.wasmtime_config_epoch_interruption_set(config, 1);
        Wasmtime.wasmtime_config_consume_fuel_set(config, consumeFuel ? (byte)1 : (byte)0);
        Wasmtime.wasmtime_config_signals_based_traps_set(config, 0);
        Wasmtime.wasmtime_config_memory_reservation_set(config, MemoryReservationBytes);
        Wasmtime.wasmtime_config_memory_guard_size_set(config, 0);
        Wasmtime.wasmtime_config_memory_reservation_for_growth_set(config, MemoryReservationForGrowthBytes);
        Wasmtime.wasmtime_config_memory_may_move_set(config, 1);
        Wasmtime.wasmtime_config_max_wasm_stack_set(config, MaxWasmStackBytes);
        Wasmtime.wasmtime_config_wasm_threads_set(config, 0);

        // The engine takes ownership of the configuration.
        var engine = Wasmtime.wasm_engine_new_with_config(config);
        if (engine == IntPtr.Zero)
        {
            throw new InvalidOperationException("Wasmtime rejected the engine configuration.");
        }

        return new EngineHandle(engine);
    }

    private unsafe LinkerHandle CreateLinker()
    {
        var linker = new LinkerHandle(Wasmtime.wasmtime_linker_new(Engine.DangerousGetHandle()));
        foreach (var function in Functions.Values)
        {
            var handle = GCHandle.Alloc(function);
            _pinned.Add(handle);
            var type = NewFuncType(function.Signature, function.HasResult);
            try
            {
                var module = Encoding.UTF8.GetBytes(function.Module);
                var name = Encoding.UTF8.GetBytes(function.Name);
                IntPtr error;
                fixed (byte* m = module)
                fixed (byte* n = name)
                {
                    error = Wasmtime.wasmtime_linker_define_func_unchecked(
                        linker.DangerousGetHandle(), m, (nuint)module.Length, n, (nuint)name.Length, type,
                        &WasmHostFunction.Trampoline, GCHandle.ToIntPtr(handle), IntPtr.Zero);
                }

                if (error != IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Wasmtime refused host function {function.Key}: {Wasmtime.TakeError(error)}");
                }
            }
            finally
            {
                Wasmtime.wasm_functype_delete(type);
            }
        }

        return linker;
    }

    private static unsafe IntPtr NewFuncType(string signature, bool hasResult)
    {
        WasmPtrVec parameters = default;
        WasmPtrVec results = default;
        if (signature.Length == 0)
        {
            Wasmtime.wasm_valtype_vec_new_empty(&parameters);
        }
        else
        {
            var kinds = stackalloc IntPtr[signature.Length];
            for (var i = 0; i < signature.Length; i++)
            {
                kinds[i] = Wasmtime.wasm_valtype_new(signature[i] == 'I' ? Wasmtime.ValKindI64 : Wasmtime.ValKindI32);
            }

            Wasmtime.wasm_valtype_vec_new(&parameters, (nuint)signature.Length, kinds);
        }

        if (hasResult)
        {
            var kind = Wasmtime.wasm_valtype_new(Wasmtime.ValKindI32);
            Wasmtime.wasm_valtype_vec_new(&results, 1, &kind);
        }
        else
        {
            Wasmtime.wasm_valtype_vec_new_empty(&results);
        }

        return Wasmtime.wasm_functype_new(&parameters, &results);
    }

    private static void StartTicker()
    {
        if (_ticker is not null)
        {
            return;
        }

        _ticker = new Thread(Tick)
        {
            IsBackground = true,
            Name = "Titanium wasm epoch",
            Priority = ThreadPriority.AboveNormal,
        };
        _ticker.Start();
    }

    /// <summary>
    /// Advances every engine's epoch by wall time. Sleeping is coarse on some hosts, so the loop adds as many ticks as
    /// really elapsed; a call's deadline therefore tracks the clock, not the scheduler.
    /// </summary>
    private static void Tick()
    {
        var clock = Stopwatch.StartNew();
        long issued = 0;
        while (true)
        {
            Thread.Sleep(1);
            var due = clock.ElapsedMilliseconds / EpochTickMilliseconds;
            WasmRuntime[] engines;
            lock (Gate)
            {
                engines = Running.ToArray();
            }

            while (issued < due)
            {
                foreach (var engine in engines)
                {
                    Wasmtime.wasmtime_engine_increment_epoch(engine.Engine.DangerousGetHandle());
                }

                issued++;
                Interlocked.Increment(ref _epochTicks);
            }
        }
    }
}
