namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>A local response a script asked for with <c>ctx.deny</c> / <c>ctx.respond</c>.</summary>
internal sealed record LuaDeny(int Status, byte[]? Body, IReadOnlyList<KeyValuePair<string, string>>? Headers);

/// <summary>What one finished call asked the host to do. Logs, metrics, and timers were already handled.</summary>
internal sealed class LuaCallResult
{
    public static readonly LuaCallResult Empty = new();

    /// <summary>Header edits in order; a null value removes the header.</summary>
    public List<KeyValuePair<string, string?>>? HeaderEdits { get; set; }

    /// <summary>Replacement body (HTTP) or data (L4), when the script set one.</summary>
    public byte[]? Body { get; set; }

    public LuaDeny? Deny { get; set; }

    public string? ClusterId { get; set; }

    /// <summary><c>ctx.vars</c> as encoded at the end of the call, handed to the next phase of the same request.</summary>
    public byte[]? Vars { get; set; }

    /// <summary>The first value the function returned (scalars and tables of scalars).</summary>
    public object? Return { get; set; }
}
