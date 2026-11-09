using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Pure mapping helpers for Lua failure codes.
///     Bug classes: limit failures do not retire state, or metrics reason strings drift.
/// </summary>
[TestClass]
public class LuaScriptExceptionBehaviorTests
{
    [TestMethod]
    [DataRow(LuaNative.ErrInstructions, LuaFailure.Instructions, "instructions", true)]
    [DataRow(LuaNative.ErrMemory, LuaFailure.Memory, "memory", true)]
    [DataRow(LuaNative.ErrDeadline, LuaFailure.Deadline, "deadline", true)]
    [DataRow(LuaNative.ErrBroken, LuaFailure.Broken, "broken", true)]
    [DataRow(LuaNative.ErrOutput, LuaFailure.Output, "output", false)]
    [DataRow(LuaNative.ErrArgument, LuaFailure.Missing, "missing", false)]
    [DataRow(LuaNative.ErrRuntime, LuaFailure.Runtime, "runtime", false)]
    [DataRow(99, LuaFailure.Runtime, "runtime", false)]
    public void FailureOf_MapsCodes_AndRetiresOnlyLimits(int code, LuaFailure failure, string reason, bool retires)
    {
        Assert.AreEqual(failure, LuaScriptException.FailureOf(code));
        Assert.AreEqual(reason, LuaScriptException.Reason(failure));
        Assert.AreEqual(retires, LuaScriptException.IsLimit(failure));
        Assert.AreEqual(retires, new LuaScriptException(failure, "x").RetiresState);
    }

    [TestMethod]
    public void Reason_CoversLoadAndTimeout()
    {
        Assert.AreEqual("load", LuaScriptException.Reason(LuaFailure.Load));
        Assert.AreEqual("timeout", LuaScriptException.Reason(LuaFailure.Timeout));
    }
}
