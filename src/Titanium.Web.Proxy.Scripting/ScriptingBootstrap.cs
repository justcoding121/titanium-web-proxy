using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting;

/// <summary>
/// Registers the scripting engines with <see cref="PluginEngines"/>. Registration is lazy: an engine is constructed, and
/// its native library loaded, only when a configuration names its kind.
/// </summary>
public static class ScriptingBootstrap
{
    /// <summary>Registers every engine this package provides. Safe to call more than once.</summary>
    public static void Register()
    {
        PluginEngines.Register(PluginKinds.Wasm, static () => new WasmPluginEngine());
    }
}
