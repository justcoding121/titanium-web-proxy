using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Pure <see cref="LuaOptions.TryParse"/> rejection paths (no Lua native).
///     Bug classes: missing file accepted, wasm fuel used for lua, deadline >= timeout.
/// </summary>
[TestClass]
public class LuaOptionsParseBehaviorTests
{
    [TestMethod]
    public void TryParse_RejectsMissingFile_Fuel_BadBodyMode_AndDeadline()
    {
        Assert.IsFalse(LuaOptions.TryParse(new PluginConfig { Name = "x" }, out _, out var noFile));
        Assert.AreEqual("no file is set", noFile);

        Assert.IsFalse(LuaOptions.TryParse(new PluginConfig { Name = "x", File = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".lua") },
            out _, out var missing));
        StringAssert.Contains(missing!, "does not exist");

        var path = Path.Combine(Path.GetTempPath(), "twp-lua-opts-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, "-- empty");
        try
        {
            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { Fuel = 1 },
            }, out _, out var fuel));
            StringAssert.Contains(fuel!, "limits.fuel");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                BodyMode = "chunked",
            }, out _, out var body));
            StringAssert.Contains(body!, "bodyMode");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                TimeoutMs = 1000,
                Limits = new PluginLimitsConfig { EpochDeadlineMs = 1000 },
            }, out _, out var deadline));
            StringAssert.Contains(deadline!, "epochDeadlineMs");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Settings = new Dictionary<string, string> { [LuaOptions.SharedDictsSetting] = "bad name" },
            }, out _, out var dicts));
            Assert.IsFalse(string.IsNullOrWhiteSpace(dicts));

            Assert.IsTrue(LuaOptions.TryParse(new PluginConfig { Name = "ok", File = path }, out var options, out var okError));
            Assert.IsNull(okError);
            Assert.AreEqual("ok", options!.Name);
            Assert.AreEqual(ScriptBodyMode.Streaming, options.BodyMode);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
