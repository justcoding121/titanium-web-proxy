using System.Security.Cryptography;
using Titanium.Web.Proxy.Scripting.Wasm.Native;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>An exported function's shape: how many i32/i64 parameters and results it has.</summary>
internal readonly record struct WasmExportShape(string Parameters, int Results);

/// <summary>
/// A compiled, validated proxy-wasm module ready to instantiate. Validation happens once, before any VM exists: the ABI
/// marker must name a supported version, and every import must be one the host provides with the same signature.
/// </summary>
internal sealed class WasmModule : IDisposable
{
    private WasmModule(WasmRuntime runtime, WasmModuleHandle module, InstancePreHandle pre, string hash, string abi,
        IReadOnlyDictionary<string, WasmExportShape> exports, bool fromCache)
    {
        Runtime = runtime;
        Handle = module;
        InstancePre = pre;
        Hash = hash;
        AbiVersion = abi;
        Exports = exports;
        FromCache = fromCache;
    }

    public WasmRuntime Runtime { get; }

    public WasmModuleHandle Handle { get; }

    public InstancePreHandle InstancePre { get; }

    /// <summary>SHA-256 of the module bytes, lower-case hex.</summary>
    public string Hash { get; }

    /// <summary>The <see cref="ProxyWasmAbi"/> marker the module carries.</summary>
    public string AbiVersion { get; }

    /// <summary>Function exports by name.</summary>
    public IReadOnlyDictionary<string, WasmExportShape> Exports { get; }

    /// <summary>True when the machine code came from the serialized-module cache instead of a fresh compile.</summary>
    public bool FromCache { get; }

    public bool HasExport(string name) => Exports.ContainsKey(name);

    /// <summary>Compiles and validates <paramref name="bytes"/>.</summary>
    public static unsafe WasmModule Load(WasmRuntime runtime, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        WasmModuleHandle? module = null;
        var fromCache = false;
        if (module is null)
        {
            IntPtr raw;
            IntPtr error;
            fixed (byte* p = bytes)
            {
                error = Wasmtime.wasmtime_module_new(runtime.Engine.DangerousGetHandle(), p, (nuint)bytes.Length, &raw);
            }

            if (error != IntPtr.Zero)
            {
                throw new InvalidOperationException("the module does not compile: " + Wasmtime.TakeError(error));
            }

            module = new WasmModuleHandle(raw);
        }

        try
        {
            var exports = ReadExports(module);
            if (!ProxyWasmAbi.TryDetect(exports.Keys, out var abi, out var abiError))
            {
                throw new InvalidOperationException(abiError);
            }

            CheckImports(runtime, module);

            IntPtr pre;
            var linkError = Wasmtime.wasmtime_linker_instantiate_pre(runtime.Linker.DangerousGetHandle(), module.DangerousGetHandle(), &pre);
            if (linkError != IntPtr.Zero)
            {
                throw new InvalidOperationException("the module does not link: " + Wasmtime.TakeError(linkError));
            }

            return new WasmModule(runtime, module, new InstancePreHandle(pre), hash, abi!, exports, fromCache);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        InstancePre.Dispose();
        Handle.Dispose();
    }

    private static unsafe Dictionary<string, WasmExportShape> ReadExports(WasmModuleHandle module)
    {
        var result = new Dictionary<string, WasmExportShape>(StringComparer.Ordinal);
        WasmPtrVec exports = default;
        Wasmtime.wasmtime_module_exports(module.DangerousGetHandle(), &exports);
        try
        {
            for (nuint i = 0; i < exports.Size; i++)
            {
                var export = exports.Data[i];
                var name = Wasmtime.ReadName(Wasmtime.wasm_exporttype_name(export));
                var type = Wasmtime.wasm_exporttype_type(export);
                if (Wasmtime.wasm_externtype_kind(type) != Wasmtime.ExternKindFunc)
                {
                    continue;
                }

                var func = Wasmtime.wasm_externtype_as_functype_const(type);
                result[name] = new WasmExportShape(Kinds(Wasmtime.wasm_functype_params(func)), (int)Wasmtime.wasm_functype_results(func)->Size);
            }
        }
        finally
        {
            Wasmtime.wasm_exporttype_vec_delete(&exports);
        }

        return result;
    }

    /// <summary>Fails naming the first import the host does not provide, or provides with another signature.</summary>
    private static unsafe void CheckImports(WasmRuntime runtime, WasmModuleHandle module)
    {
        WasmPtrVec imports = default;
        Wasmtime.wasmtime_module_imports(module.DangerousGetHandle(), &imports);
        try
        {
            for (nuint i = 0; i < imports.Size; i++)
            {
                var import = imports.Data[i];
                var moduleName = Wasmtime.ReadName(Wasmtime.wasm_importtype_module(import));
                var name = Wasmtime.ReadName(Wasmtime.wasm_importtype_name(import));
                var type = Wasmtime.wasm_importtype_type(import);
                if (Wasmtime.wasm_externtype_kind(type) != Wasmtime.ExternKindFunc)
                {
                    throw new InvalidOperationException(
                        $"the module imports {moduleName}.{name}, which is not a function; the host provides only functions (the module must export its own memory)");
                }

                if (!runtime.Functions.TryGetValue(moduleName + "." + name, out var function))
                {
                    throw new InvalidOperationException($"missing import {moduleName}.{name}: the Titanium proxy-wasm host does not provide it");
                }

                var func = Wasmtime.wasm_externtype_as_functype_const(type);
                var parameters = Kinds(Wasmtime.wasm_functype_params(func));
                var results = Kinds(Wasmtime.wasm_functype_results(func));
                if (!function.Matches(parameters, results))
                {
                    throw new InvalidOperationException(
                        $"import {moduleName}.{name}({parameters}){(results.Length > 0 ? "->" + results : string.Empty)} does not match the host's {function.Describe()}");
                }
            }
        }
        finally
        {
            Wasmtime.wasm_importtype_vec_delete(&imports);
        }
    }

    private static unsafe string Kinds(WasmPtrVec* types)
    {
        if (types is null || types->Size == 0)
        {
            return string.Empty;
        }

        var chars = new char[(int)types->Size];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Wasmtime.wasm_valtype_kind(types->Data[i]) switch
            {
                Wasmtime.ValKindI32 => 'i',
                Wasmtime.ValKindI64 => 'I',
                Wasmtime.ValKindF32 => 'f',
                Wasmtime.ValKindF64 => 'F',
                _ => '?',
            };
        }

        return new string(chars);
    }
}
