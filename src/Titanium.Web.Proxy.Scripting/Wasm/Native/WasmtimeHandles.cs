using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Scripting.Wasm.Native;

/// <summary>Base for Wasmtime objects owned by .NET: released exactly once, never while a call holds a reference.</summary>
internal abstract class WasmtimeHandle : SafeHandle
{
    protected WasmtimeHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;
}

internal sealed class EngineHandle(IntPtr handle) : WasmtimeHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        Wasmtime.wasm_engine_delete(handle);
        return true;
    }
}

internal sealed class WasmModuleHandle(IntPtr handle) : WasmtimeHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        Wasmtime.wasmtime_module_delete(handle);
        return true;
    }
}

internal sealed class LinkerHandle(IntPtr handle) : WasmtimeHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        Wasmtime.wasmtime_linker_delete(handle);
        return true;
    }
}

internal sealed class InstancePreHandle(IntPtr handle) : WasmtimeHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        Wasmtime.wasmtime_instance_pre_delete(handle);
        return true;
    }
}

internal sealed class StoreHandle(IntPtr handle) : WasmtimeHandle(handle)
{
    protected override bool ReleaseHandle()
    {
        Wasmtime.wasmtime_store_delete(handle);
        return true;
    }
}
