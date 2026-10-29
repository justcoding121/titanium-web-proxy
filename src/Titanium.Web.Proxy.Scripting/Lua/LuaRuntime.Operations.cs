namespace Titanium.Web.Proxy.Scripting.Lua;

internal sealed partial class LuaRuntime
{
    /// <summary>Host operations a script may wait on. Null means the operation is not available.</summary>
    private partial ValueTask<byte[]?> RunOperationAsync(string? kind, LuaTable? op, CancellationToken token) => new((byte[]?)null);
}
