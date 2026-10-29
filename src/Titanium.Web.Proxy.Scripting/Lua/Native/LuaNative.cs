using System.Reflection;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Scripting.Lua.Native;

/// <summary>
/// Bindings to the Titanium Lua shim (<c>tools/scripting/lua-shim</c>), linked with Lua 5.4 into one native library named
/// <c>lua54</c>. Lua never calls back into managed code: a call passes an encoded snapshot in and gets an encoded command
/// buffer out, so a Lua error (a <c>longjmp</c>) never crosses a managed frame.
/// </summary>
internal static unsafe partial class LuaNative
{
    public const string Library = "lua54";

    /// <summary>The shim ABI this binding speaks (<c>TWP_ABI_VERSION</c>).</summary>
    public const int AbiVersion = 1;

    /// <summary>Folder searched before the default probing paths; for development builds and tests.</summary>
    public const string NativeDirVariable = "TITANIUM_SCRIPTING_NATIVE_DIR";

    public const int Ok = 0;
    public const int Yield = 1;
    public const int ErrRuntime = -1;
    public const int ErrInstructions = -2;
    public const int ErrMemory = -3;
    public const int ErrDeadline = -4;
    public const int ErrArgument = -5;
    public const int ErrOutput = -6;
    public const int ErrBroken = -7;

    private static readonly object Gate = new();
    private static bool _resolverSet;
    private static bool? _available;
    private static string? _error;
    private static string? _version;

    /// <summary>The Lua release the shim was built from, once <see cref="TryLoad"/> succeeded.</summary>
    public static string? LuaVersion => _version;

    /// <summary>
    /// Loads the native library and checks it is the shim (not a stock Lua build) with the expected ABI. Never throws; the
    /// result is cached, so a missing library is reported the same way every time.
    /// </summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (_available is { } known)
            {
                error = _error;
                return known;
            }

            _available = Probe(out _error);
            error = _error;
            return _available.Value;
        }
    }

    private static bool Probe(out string? error)
    {
        EnsureResolver();
        if (!TryLoadLibrary(out var handle))
        {
            error = $"the Lua engine needs the '{FileName()}' native library beside the application (see 'titanium scripting-deps')";
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, "twp_abi_version", out _))
        {
            error = $"'{FileName()}' is a plain Lua build without the Titanium shim; build it with tools/scripting/build-lua-shim.ps1";
            return false;
        }

        try
        {
            var abi = twp_abi_version();
            if (abi != AbiVersion)
            {
                error = $"'{FileName()}' speaks shim ABI {abi}; this build needs ABI {AbiVersion}";
                return false;
            }

            _version = Marshal.PtrToStringUTF8(twp_lua_version());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"'{FileName()}' could not be used: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    private static void EnsureResolver()
    {
        if (_resolverSet)
        {
            return;
        }

        _resolverSet = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LuaNative).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Another component set the resolver for this assembly; default probing still applies.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && TryLoadLibrary(out var handle) ? handle : IntPtr.Zero;

    private static bool TryLoadLibrary(out IntPtr handle)
    {
        if (Environment.GetEnvironmentVariable(NativeDirVariable) is { Length: > 0 } dir &&
            NativeLibrary.TryLoad(Path.Combine(dir, FileName()), out handle))
        {
            return true;
        }

        if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, FileName()), out handle))
        {
            return true;
        }

        return NativeLibrary.TryLoad(Library, typeof(LuaNative).Assembly, DllImportSearchPath.SafeDirectories, out handle);
    }

    public static string FileName() =>
        OperatingSystem.IsWindows() ? Library + ".dll" : OperatingSystem.IsMacOS() ? "lib" + Library + ".dylib" : "lib" + Library + ".so";

    [LibraryImport(Library)]
    internal static partial int twp_abi_version();

    [LibraryImport(Library)]
    internal static partial IntPtr twp_lua_version();

    [LibraryImport(Library)]
    internal static partial IntPtr twp_create(long memLimit, long outLimit, byte* err, int errLen);

    [LibraryImport(Library)]
    internal static partial void twp_destroy(IntPtr state);

    [LibraryImport(Library)]
    internal static partial long twp_memory_used(LuaStateHandle state);

    [LibraryImport(Library)]
    internal static partial void twp_buffer_free(IntPtr buffer);

    [LibraryImport(Library)]
    internal static partial int twp_load(LuaStateHandle state, byte* source, nuint length, byte* chunkName, long budget, long maxMicros,
        byte* err, int errLen);

    [LibraryImport(Library)]
    internal static partial int twp_set_settings(LuaStateHandle state, byte* data, nuint length);

    [LibraryImport(Library)]
    internal static partial int twp_has_function(LuaStateHandle state, byte* name);

    [LibraryImport(Library)]
    internal static partial int twp_start(LuaStateHandle state, byte* function, byte* snapshot, nuint length, long budget, long maxMicros,
        int* callId, IntPtr* output, nuint* outputLength, long* used);

    [LibraryImport(Library)]
    internal static partial int twp_resume(LuaStateHandle state, int callId, byte* values, nuint length, long budget, long maxMicros,
        IntPtr* output, nuint* outputLength, long* used);

    [LibraryImport(Library)]
    internal static partial void twp_cancel(LuaStateHandle state, int callId);

    [LibraryImport(Library)]
    internal static partial int twp_dict_configure(byte* name, long maxItems, long maxBytes);

    [LibraryImport(Library)]
    internal static partial long twp_dict_count(byte* name);

    [LibraryImport(Library)]
    internal static partial int twp_dict_flush(byte* name);
}
