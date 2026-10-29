using System.Text;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// A proxy-wasm context: a root context per plugin per VM, or a stream context (HTTP request or L4 connection) that lives
/// in one VM for its whole life. Every member is used with the VM's gate held.
/// </summary>
internal abstract class WasmContext(WasmVm vm, int id, int rootId, WasmPlugin plugin)
{
    public WasmVm Vm { get; } = vm;

    public int Id { get; } = id;

    /// <summary>The root context this one belongs to; equal to <see cref="Id"/> for a root context.</summary>
    public int RootId { get; } = rootId;

    public WasmPlugin Plugin { get; } = plugin;

    /// <summary>Values the guest stored with <c>proxy_set_property</c>, by NUL-joined path.</summary>
    public Dictionary<string, byte[]> Properties { get; } = new(StringComparer.Ordinal);

    public virtual byte[]? GetBuffer(ProxyBufferType type) => type switch
    {
        ProxyBufferType.VmConfiguration => Plugin.VmConfiguration,
        ProxyBufferType.PluginConfiguration => Plugin.Configuration,
        _ => null,
    };

    /// <summary>Replaces <paramref name="size"/> bytes at <paramref name="start"/> of a buffer the context owns.</summary>
    public virtual int SetBuffer(ProxyBufferType type, int start, int size, byte[] data) => WasmResult.NotFound;

    public virtual WasmHeaderMap? GetMap(ProxyMapType type) => null;

    public virtual int SendLocalResponse(int status, string details, byte[] body, List<KeyValuePair<string, string>> headers, int grpcStatus) =>
        WasmResult.BadArgument;

    public virtual int ContinueStream(ProxyStreamType type) => WasmResult.BadArgument;

    public virtual int CloseStream(ProxyStreamType type) => WasmResult.BadArgument;

    /// <summary>A property the context itself answers (request, response, connection). Null falls through to plugin-level ones.</summary>
    public virtual byte[]? GetContextProperty(string path) => null;

    /// <summary>The VM is going away; release anything that waits on it.</summary>
    public virtual void OnVmDisposed()
    {
    }

    /// <summary>Splices <paramref name="data"/> over <paramref name="size"/> bytes at <paramref name="start"/>, clamped to the buffer.</summary>
    protected static byte[] Splice(byte[] buffer, int start, int size, byte[] data)
    {
        start = Math.Clamp(start, 0, buffer.Length);
        size = Math.Clamp(size, 0, buffer.Length - start);
        var result = new byte[buffer.Length - size + data.Length];
        buffer.AsSpan(0, start).CopyTo(result);
        data.CopyTo(result, start);
        buffer.AsSpan(start + size).CopyTo(result.AsSpan(start + data.Length));
        return result;
    }

    protected static byte[] Utf8(string? value) => value is null ? [] : Encoding.UTF8.GetBytes(value);
}

/// <summary>The root context of one plugin in one VM. Owns the plugin's tick timer in that VM.</summary>
internal sealed class WasmRootContext(WasmVm vm, int id, WasmPlugin plugin) : WasmContext(vm, id, id, plugin)
{
    /// <summary>Period the guest asked for with <c>proxy_set_tick_period_milliseconds</c>; zero is off.</summary>
    public int TickPeriodMs { get; set; }

    public Timer? Ticker { get; set; }

    public override void OnVmDisposed()
    {
        Ticker?.Dispose();
        Ticker = null;
    }
}
