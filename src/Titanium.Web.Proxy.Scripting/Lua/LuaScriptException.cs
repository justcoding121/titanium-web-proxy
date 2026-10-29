using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>Why a Lua call failed. The limit kinds also retire the state that hit them.</summary>
internal enum LuaFailure
{
    Load,
    Runtime,
    Instructions,
    Memory,
    Deadline,
    Output,
    Broken,
    Missing,
    Timeout,
}

internal sealed class LuaScriptException : Exception
{
    public LuaScriptException(LuaFailure failure, string message)
        : base(message) => Failure = failure;

    public LuaFailure Failure { get; }

    /// <summary>True when the state is no longer trusted and must be replaced.</summary>
    public bool RetiresState => IsLimit(Failure);

    public static bool IsLimit(LuaFailure failure) =>
        failure is LuaFailure.Instructions or LuaFailure.Memory or LuaFailure.Deadline or LuaFailure.Broken;

    public static LuaFailure FailureOf(int code) => code switch
    {
        LuaNative.ErrInstructions => LuaFailure.Instructions,
        LuaNative.ErrMemory => LuaFailure.Memory,
        LuaNative.ErrDeadline => LuaFailure.Deadline,
        LuaNative.ErrOutput => LuaFailure.Output,
        LuaNative.ErrBroken => LuaFailure.Broken,
        LuaNative.ErrArgument => LuaFailure.Missing,
        _ => LuaFailure.Runtime,
    };

    public static string Reason(LuaFailure failure) => failure switch
    {
        LuaFailure.Instructions => "instructions",
        LuaFailure.Memory => "memory",
        LuaFailure.Deadline => "deadline",
        LuaFailure.Output => "output",
        LuaFailure.Broken => "broken",
        LuaFailure.Missing => "missing",
        LuaFailure.Load => "load",
        LuaFailure.Timeout => "timeout",
        _ => "runtime",
    };
}
