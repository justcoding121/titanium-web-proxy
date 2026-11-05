using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// The <c>kind: lua</c> plugin engine: Lua 5.4 scripts in sandboxed states with in-engine instruction, memory, and CPU-time
/// limits. The native library loads the first time a Lua plugin is configured. A plugin is fully built (every pooled state
/// compiled and <c>on_configure</c> run) before it is returned, so a reload either swaps in a working plugin or keeps the
/// old one.
/// </summary>
/// <remarks>
/// <para><c>settings</c> are visible to the script as the global <c>settings</c> table (and <c>ctx.settings</c>).</para>
/// <para><c>bodyMode</c> is <c>streaming</c> (default) or <c>buffered</c>. <c>limits.memoryMb</c>, <c>limits.maxInstructions</c>,
/// <c>limits.poolSize</c>, and <c>limits.epochDeadlineMs</c> (CPU time per call; defaults to 80% of <c>timeoutMs</c>) bound
/// each call.</para>
/// </remarks>
public sealed class LuaPluginEngine : IPluginEngine
{
    public string Kind => PluginKinds.Lua;

    public bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        middleware = null;

        if (!LuaOptions.TryParse(config, out var options, out error))
        {
            return false;
        }

        if (!LuaNative.TryLoad(out error))
        {
            return false;
        }

        try
        {
            middleware = new LuaPlugin(LuaRuntime.Create(options!, logger));
            error = null;
            return true;
        }
        catch (LuaScriptException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
