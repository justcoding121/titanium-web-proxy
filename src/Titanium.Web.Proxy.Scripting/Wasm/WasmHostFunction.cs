using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>A host function body. Reads its arguments from <paramref name="args"/> and returns the i32 result, if any.</summary>
internal unsafe delegate int WasmHostHandler(WasmVm vm, WasmValRaw* args);

/// <summary>Raised by a host function to trap the guest (for example <c>proc_exit</c>). Any other exception becomes an error code.</summary>
internal sealed class WasmTrapException(string message) : Exception(message);

/// <summary>A guest pointer or length that falls outside linear memory. Becomes the function's memory-fault code.</summary>
internal sealed class WasmMemoryException(string message) : Exception(message);

/// <summary>
/// One import the host provides. <paramref name="Signature"/> lists parameter kinds (<c>i</c> = i32, <c>I</c> = i64);
/// <paramref name="HasResult"/> adds one i32 result. <paramref name="ErrorCode"/> is returned when the handler throws, and
/// <paramref name="MemoryFaultCode"/> when it touches memory outside the guest's bounds.
/// </summary>
internal sealed unsafe record WasmHostFunction(
    string Module,
    string Name,
    string Signature,
    bool HasResult,
    WasmHostHandler Handler,
    int ErrorCode = WasmResult.InternalFailure,
    int MemoryFaultCode = WasmResult.InvalidMemoryAccess)
{
    public string Key => Module + "." + Name;

    /// <summary>Whether an import of this shape matches, so a mismatched module fails with a clear message.</summary>
    public bool Matches(string parameters, string results) =>
        string.Equals(parameters, Signature, StringComparison.Ordinal) &&
        string.Equals(results, HasResult ? "i" : string.Empty, StringComparison.Ordinal);

    public string Describe() => $"{Key}({Signature}){(HasResult ? "->i" : string.Empty)}";

    /// <summary>
    /// The single native entry point for every host function. It never lets an exception cross into Wasmtime: a trap
    /// request becomes a trap, a memory fault or any other failure becomes the function's error code.
    /// </summary>
    [UnmanagedCallersOnly]
    internal static IntPtr Trampoline(IntPtr env, IntPtr caller, WasmValRaw* args, nuint count)
    {
        try
        {
            var function = (WasmHostFunction)GCHandle.FromIntPtr(env).Target!;
            var context = Wasmtime.wasmtime_caller_context(caller);
            var data = Wasmtime.wasmtime_context_get_data(context);
            if (data == IntPtr.Zero || GCHandle.FromIntPtr(data).Target is not WasmVm vm)
            {
                return Wasmtime.NewTrap("host function called on a store with no VM");
            }

            int result;
            try
            {
                result = function.Handler(vm, args);
            }
            catch (WasmTrapException trap)
            {
                return Wasmtime.NewTrap(trap.Message);
            }
            catch (WasmMemoryException)
            {
                result = function.MemoryFaultCode;
            }
            catch (Exception ex)
            {
                vm.ReportHostError(function.Key, ex);
                result = function.ErrorCode;
            }

            if (function.HasResult && count > 0)
            {
                args[0].I32 = result;
            }

            return IntPtr.Zero;
        }
        catch (Exception)
        {
            return TrapNoThrow();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IntPtr TrapNoThrow()
    {
        try
        {
            return Wasmtime.NewTrap("host function failed");
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
