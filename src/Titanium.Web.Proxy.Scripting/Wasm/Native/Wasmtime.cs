using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Scripting.Wasm.Native;

/// <summary><c>wasm_byte_vec_t</c>, also used for <c>wasm_name_t</c> and <c>wasm_message_t</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WasmByteVec
{
    public nuint Size;
    public byte* Data;
}

/// <summary>A <c>wasm_*_vec_t</c> of pointers (import types, export types, value types).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WasmPtrVec
{
    public nuint Size;
    public IntPtr* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WasmtimeFunc
{
    public ulong StoreId;
    public IntPtr Private;
}

/// <summary>
/// <c>wasmtime_memory_t</c>. The C type nests <c>{ store_id; __private1; }</c> in an 8-aligned anonymous struct, so that
/// part is 16 bytes and <c>__private2</c> sits at offset 16.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct WasmtimeMemory
{
    [FieldOffset(0)] public ulong StoreId;
    [FieldOffset(8)] public uint Private1;
    [FieldOffset(16)] public uint Private2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WasmtimeInstance
{
    public ulong StoreId;
    public nuint Private;
}

/// <summary>
/// <c>wasmtime_extern_t</c>: a one-byte kind and an 8-aligned union whose largest member is 24 bytes. The struct is sized
/// with room to spare so a native write never runs past it.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct WasmtimeExtern
{
    public const byte KindFunc = 0;
    public const byte KindMemory = 3;

    [FieldOffset(0)] public byte Kind;
    [FieldOffset(8)] public WasmtimeFunc Func;
    [FieldOffset(8)] public WasmtimeMemory Memory;
}

/// <summary><c>wasmtime_val_raw_t</c>: 16 bytes, the unchecked call ABI.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct WasmValRaw
{
    [FieldOffset(0)] public int I32;
    [FieldOffset(0)] public long I64;
    [FieldOffset(0)] public float F32;
    [FieldOffset(0)] public double F64;
}

/// <summary>
/// Hand-written bindings to the parts of the Wasmtime C API the proxy-wasm host uses. Every signature mirrors the
/// pinned v50 headers; booleans travel as bytes so no call needs marshalling.
/// </summary>
internal static unsafe partial class Wasmtime
{
    internal const string Library = "wasmtime";

    /// <summary>The pinned C API version (tools/packaging/scripting-native.lock.json). Part of the module cache key.</summary>
    internal const string Version = "50.0.0";

    internal const byte ValKindI32 = 0;
    internal const byte ValKindI64 = 1;
    internal const byte ValKindF32 = 2;
    internal const byte ValKindF64 = 3;

    internal const byte ExternKindFunc = 0;
    internal const byte ExternKindMemory = 3;

    internal const byte TrapCodeInterrupt = 10;
    internal const byte TrapCodeOutOfFuel = 11;

    // Config and engine.
    [LibraryImport(Library)] internal static partial IntPtr wasm_config_new();
    [LibraryImport(Library)] internal static partial IntPtr wasm_engine_new_with_config(IntPtr config);
    [LibraryImport(Library)] internal static partial void wasm_engine_delete(IntPtr engine);
    [LibraryImport(Library)] internal static partial void wasmtime_engine_increment_epoch(IntPtr engine);
    [LibraryImport(Library)] internal static partial void wasmtime_config_epoch_interruption_set(IntPtr config, byte value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_consume_fuel_set(IntPtr config, byte value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_signals_based_traps_set(IntPtr config, byte value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_memory_reservation_set(IntPtr config, ulong value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_memory_guard_size_set(IntPtr config, ulong value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_memory_reservation_for_growth_set(IntPtr config, ulong value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_memory_may_move_set(IntPtr config, byte value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_max_wasm_stack_set(IntPtr config, nuint value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_wasm_threads_set(IntPtr config, byte value);
    [LibraryImport(Library)] internal static partial void wasmtime_config_parallel_compilation_set(IntPtr config, byte value);

    // Errors and traps.
    [LibraryImport(Library)] internal static partial void wasmtime_error_delete(IntPtr error);
    [LibraryImport(Library)] internal static partial void wasmtime_error_message(IntPtr error, WasmByteVec* message);
    [LibraryImport(Library)] internal static partial byte wasmtime_error_exit_status(IntPtr error, int* status);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_trap_new(byte* message, nuint length);
    [LibraryImport(Library)] internal static partial void wasm_trap_delete(IntPtr trap);
    [LibraryImport(Library)] internal static partial void wasm_trap_message(IntPtr trap, WasmByteVec* message);
    [LibraryImport(Library)] internal static partial byte wasmtime_trap_code(IntPtr trap, byte* code);
    [LibraryImport(Library)] internal static partial void wasm_byte_vec_delete(WasmByteVec* vec);

    // Text format (tests and diagnostics).
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_wat2wasm(byte* wat, nuint length, WasmByteVec* result);

    // Modules and their types.
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_module_new(IntPtr engine, byte* wasm, nuint length, IntPtr* module);
    [LibraryImport(Library)] internal static partial void wasmtime_module_delete(IntPtr module);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_module_serialize(IntPtr module, WasmByteVec* result);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_module_deserialize(IntPtr engine, byte* bytes, nuint length, IntPtr* module);
    [LibraryImport(Library)] internal static partial void wasmtime_module_imports(IntPtr module, WasmPtrVec* result);
    [LibraryImport(Library)] internal static partial void wasmtime_module_exports(IntPtr module, WasmPtrVec* result);
    [LibraryImport(Library)] internal static partial void wasm_importtype_vec_delete(WasmPtrVec* vec);
    [LibraryImport(Library)] internal static partial void wasm_exporttype_vec_delete(WasmPtrVec* vec);
    [LibraryImport(Library)] internal static partial WasmByteVec* wasm_importtype_module(IntPtr importType);
    [LibraryImport(Library)] internal static partial WasmByteVec* wasm_importtype_name(IntPtr importType);
    [LibraryImport(Library)] internal static partial IntPtr wasm_importtype_type(IntPtr importType);
    [LibraryImport(Library)] internal static partial WasmByteVec* wasm_exporttype_name(IntPtr exportType);
    [LibraryImport(Library)] internal static partial IntPtr wasm_exporttype_type(IntPtr exportType);
    [LibraryImport(Library)] internal static partial byte wasm_externtype_kind(IntPtr externType);
    [LibraryImport(Library)] internal static partial IntPtr wasm_externtype_as_functype_const(IntPtr externType);
    [LibraryImport(Library)] internal static partial WasmPtrVec* wasm_functype_params(IntPtr funcType);
    [LibraryImport(Library)] internal static partial WasmPtrVec* wasm_functype_results(IntPtr funcType);
    [LibraryImport(Library)] internal static partial byte wasm_valtype_kind(IntPtr valType);
    [LibraryImport(Library)] internal static partial IntPtr wasm_valtype_new(byte kind);
    [LibraryImport(Library)] internal static partial void wasm_valtype_vec_new(WasmPtrVec* result, nuint size, IntPtr* data);
    [LibraryImport(Library)] internal static partial void wasm_valtype_vec_new_empty(WasmPtrVec* result);
    [LibraryImport(Library)] internal static partial IntPtr wasm_functype_new(WasmPtrVec* parameters, WasmPtrVec* results);
    [LibraryImport(Library)] internal static partial void wasm_functype_delete(IntPtr funcType);

    // Linker.
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_linker_new(IntPtr engine);
    [LibraryImport(Library)] internal static partial void wasmtime_linker_delete(IntPtr linker);
    [LibraryImport(Library)]
    internal static partial IntPtr wasmtime_linker_define_func_unchecked(
        IntPtr linker, byte* module, nuint moduleLength, byte* name, nuint nameLength, IntPtr funcType,
        delegate* unmanaged<IntPtr, IntPtr, WasmValRaw*, nuint, IntPtr> callback, IntPtr data, IntPtr finalizer);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_linker_instantiate_pre(IntPtr linker, IntPtr module, IntPtr* instancePre);
    [LibraryImport(Library)] internal static partial void wasmtime_instance_pre_delete(IntPtr instancePre);
    [LibraryImport(Library)]
    internal static partial IntPtr wasmtime_instance_pre_instantiate(IntPtr instancePre, IntPtr context, WasmtimeInstance* instance, IntPtr* trap);

    // Stores.
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_store_new(IntPtr engine, IntPtr data, IntPtr finalizer);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_store_context(IntPtr store);
    [LibraryImport(Library)]
    internal static partial void wasmtime_store_limiter(IntPtr store, long memorySize, long tableElements, long instances, long tables, long memories);
    [LibraryImport(Library)] internal static partial void wasmtime_store_delete(IntPtr store);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_context_get_data(IntPtr context);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_context_set_fuel(IntPtr context, ulong fuel);
    [LibraryImport(Library)] internal static partial void wasmtime_context_set_epoch_deadline(IntPtr context, ulong ticksBeyondCurrent);

    // Instances, functions, memory.
    [LibraryImport(Library)]
    internal static partial byte wasmtime_instance_export_get(IntPtr context, WasmtimeInstance* instance, byte* name, nuint nameLength, WasmtimeExtern* item);
    [LibraryImport(Library)]
    internal static partial IntPtr wasmtime_func_call_unchecked(IntPtr context, WasmtimeFunc* func, WasmValRaw* argsAndResults, nuint length, IntPtr* trap);
    [LibraryImport(Library)] internal static partial IntPtr wasmtime_caller_context(IntPtr caller);
    [LibraryImport(Library)] internal static partial byte* wasmtime_memory_data(IntPtr context, WasmtimeMemory* memory);
    [LibraryImport(Library)] internal static partial nuint wasmtime_memory_data_size(IntPtr context, WasmtimeMemory* memory);

    /// <summary>Reads and frees a <c>wasmtime_error_t</c>.</summary>
    internal static string TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            WasmByteVec message = default;
            wasmtime_error_message(error, &message);
            return TakeString(ref message);
        }
        finally
        {
            wasmtime_error_delete(error);
        }
    }

    /// <summary>Reads and frees a <c>wasm_trap_t</c>, with its trap code when it has one.</summary>
    internal static string TakeTrap(IntPtr trap, out byte? code)
    {
        try
        {
            byte raw;
            code = wasmtime_trap_code(trap, &raw) != 0 ? raw : null;
            WasmByteVec message = default;
            wasm_trap_message(trap, &message);
            return TakeString(ref message);
        }
        finally
        {
            wasm_trap_delete(trap);
        }
    }

    internal static IntPtr NewTrap(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        fixed (byte* p = bytes)
        {
            return wasmtime_trap_new(p, (nuint)bytes.Length);
        }
    }

    internal static string ReadName(WasmByteVec* name) =>
        name is null || name->Size == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(name->Data, checked((int)name->Size));

    private static string TakeString(ref WasmByteVec vec)
    {
        try
        {
            if (vec.Size == 0 || vec.Data is null)
            {
                return string.Empty;
            }

            var length = checked((int)vec.Size);
            // wasm_message_t is NUL-terminated by convention; drop it.
            while (length > 0 && vec.Data[length - 1] == 0)
            {
                length--;
            }

            return System.Text.Encoding.UTF8.GetString(vec.Data, length);
        }
        finally
        {
            fixed (WasmByteVec* p = &vec)
            {
                wasm_byte_vec_delete(p);
            }
        }
    }
}
