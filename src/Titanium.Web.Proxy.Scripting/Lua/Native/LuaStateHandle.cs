using System.Runtime.InteropServices;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Lua.Native;

/// <summary>Owns one shim state. The marshaller adds a reference for every call, so a state is never freed under a call.</summary>
internal sealed class LuaStateHandle : SafeHandle
{
    public LuaStateHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        LuaNative.twp_destroy(handle);
        return true;
    }

    internal static unsafe LuaStateHandle Create(long memoryLimit, long outputLimit)
    {
        var err = stackalloc byte[512];
        err[0] = 0;
        var state = new LuaStateHandle();
        var raw = LuaNative.twp_create(memoryLimit, outputLimit, err, 512);
        if (raw == IntPtr.Zero)
        {
            throw new LuaScriptException(LuaFailure.Load, Marshal.PtrToStringUTF8((IntPtr)err) ?? "could not create a Lua state");
        }

        state.SetHandle(raw);
        return state;
    }
}

/// <summary>Result of one native segment: the return code and the decoded-on-demand output buffer.</summary>
internal readonly record struct LuaSegment(int Code, int CallId, byte[] Output, long Used);

/// <summary>Thin managed calls over <see cref="LuaNative"/>. Each one copies the native output and frees it at once.</summary>
internal static unsafe class LuaCalls
{
    public static void Load(LuaStateHandle state, byte[] source, string chunkName, long budget, long maxMicros)
    {
        var err = stackalloc byte[1024];
        err[0] = 0;
        var name = Utf8Z(chunkName);
        int code;
        fixed (byte* src = source)
        fixed (byte* n = name)
        {
            code = LuaNative.twp_load(state, src, (nuint)source.Length, n, budget, maxMicros, err, 1024);
        }

        if (code != LuaNative.Ok)
        {
            throw new LuaScriptException(LuaScriptException.FailureOf(code), Marshal.PtrToStringUTF8((IntPtr)err) ?? "the script failed to load");
        }
    }

    public static bool SetSettings(LuaStateHandle state, byte[] encoded)
    {
        fixed (byte* p = encoded)
        {
            return LuaNative.twp_set_settings(state, p, (nuint)encoded.Length) == LuaNative.Ok;
        }
    }

    public static bool HasFunction(LuaStateHandle state, string name)
    {
        var bytes = Utf8Z(name);
        fixed (byte* p = bytes)
        {
            return LuaNative.twp_has_function(state, p) != 0;
        }
    }

    public static LuaSegment Start(LuaStateHandle state, string function, ReadOnlySpan<byte> snapshot, long budget, long maxMicros)
    {
        var fn = Utf8Z(function);
        int callId, code;
        IntPtr output;
        nuint length;
        long used;
        fixed (byte* f = fn)
        fixed (byte* s = snapshot)
        {
            code = LuaNative.twp_start(state, f, s, (nuint)snapshot.Length, budget, maxMicros, &callId, &output, &length, &used);
        }

        return new LuaSegment(code, callId, Take(output, length), used);
    }

    public static LuaSegment Resume(LuaStateHandle state, int callId, ReadOnlySpan<byte> values, long budget, long maxMicros)
    {
        int code;
        IntPtr output;
        nuint length;
        long used;
        fixed (byte* v = values)
        {
            code = LuaNative.twp_resume(state, callId, v, (nuint)values.Length, budget, maxMicros, &output, &length, &used);
        }

        return new LuaSegment(code, callId, Take(output, length), used);
    }

    public static void Cancel(LuaStateHandle state, int callId) => LuaNative.twp_cancel(state, callId);

    public static long MemoryUsed(LuaStateHandle state) => LuaNative.twp_memory_used(state);

    public static void DictConfigure(string name, long maxItems, long maxBytes)
    {
        var bytes = Utf8Z(name);
        fixed (byte* p = bytes)
        {
            LuaNative.twp_dict_configure(p, maxItems, maxBytes);
        }
    }

    public static long DictCount(string name)
    {
        var bytes = Utf8Z(name);
        fixed (byte* p = bytes)
        {
            return LuaNative.twp_dict_count(p);
        }
    }

    public static void DictFlush(string name)
    {
        var bytes = Utf8Z(name);
        fixed (byte* p = bytes)
        {
            LuaNative.twp_dict_flush(p);
        }
    }

    private static byte[] Take(IntPtr output, nuint length)
    {
        if (output == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            return new ReadOnlySpan<byte>((void*)output, checked((int)length)).ToArray();
        }
        finally
        {
            LuaNative.twp_buffer_free(output);
        }
    }

    private static byte[] Utf8Z(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }
}
