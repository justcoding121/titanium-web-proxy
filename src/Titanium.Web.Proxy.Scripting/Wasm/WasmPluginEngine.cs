using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The <c>kind: wasm</c> plugin engine: proxy-wasm modules (ABI 0.1.0, 0.2.0, 0.2.1) run on Wasmtime. The native
/// library loads the first time a WASM plugin is configured. A plugin is fully built (compiled, every VM instantiated,
/// <c>on_vm_start</c> and <c>on_configure</c> accepted) before it is returned, so a reload either swaps in a working plugin
/// or keeps the old one.
/// </summary>
/// <remarks>
/// <para><c>settings.configuration</c> is the plugin configuration the guest reads in <c>on_configure</c>; with no such key
/// and other settings present, the settings are passed as a JSON object. <c>settings.vmConfiguration</c> is the VM
/// configuration for <c>on_vm_start</c>.</para>
/// <para><c>bodyMode</c> is <c>streaming</c> (default) or <c>buffered</c>. A file ending in <c>.wat</c> is compiled from text.</para>
/// </remarks>
public sealed class WasmPluginEngine : IPluginEngine
{
    public const string ConfigurationSetting = "configuration";
    public const string VmConfigurationSetting = "vmConfiguration";

    public string Kind => PluginKinds.Wasm;

    public bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        middleware = null;
        var name = config.Name ?? "wasm";

        if (string.IsNullOrWhiteSpace(config.File))
        {
            error = "no file is set";
            return false;
        }

        var path = Path.GetFullPath(config.File);
        if (!File.Exists(path))
        {
            error = $"'{config.File}' does not exist";
            return false;
        }

        if (!WasmLimits.TryParse(config, out var limits, out error))
        {
            return false;
        }

        if (!TryParseBodyMode(config.BodyMode, out var bodyMode, out error))
        {
            return false;
        }

        if (!WasmRuntime.TryLoadNative(out error))
        {
            return false;
        }

        WasmVmPool? pool = null;
        try
        {
            var runtime = WasmRuntime.Get(limits.Fuel is not null);
            var bytes = File.ReadAllBytes(path);
            if (path.EndsWith(".wat", StringComparison.OrdinalIgnoreCase))
            {
                bytes = WasmRuntime.WatToWasm(Encoding.UTF8.GetString(bytes));
            }

            var module = WasmModule.Load(runtime, bytes);
            pool = new WasmVmPool(name, module, limits, logger);
            var plugin = new WasmPlugin(name, config.VmId, pool, limits, bodyMode,
                string.Equals(config.FailMode, ProxyPluginContract.FailOpen, StringComparison.OrdinalIgnoreCase),
                PluginConfiguration(config.Settings), VmConfiguration(config.Settings), logger);
            pool.Attach(plugin);
            pool.Start();
            middleware = plugin;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or WasmVmFaultException or IOException or UnauthorizedAccessException)
        {
            pool?.Dispose();
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseBodyMode(string? value, out ScriptBodyMode mode, out string? error)
    {
        error = null;
        mode = ScriptBodyMode.Streaming;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "streaming", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "buffered", StringComparison.OrdinalIgnoreCase))
        {
            mode = ScriptBodyMode.Buffered;
            return true;
        }

        error = $"bodyMode '{value}' is not 'streaming' or 'buffered'";
        return false;
    }

    private static byte[] PluginConfiguration(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return [];
        }

        if (settings.TryGetValue(ConfigurationSetting, out var configuration))
        {
            return Encoding.UTF8.GetBytes(configuration);
        }

        var rest = settings.Where(s => s.Key != VmConfigurationSetting).ToDictionary(s => s.Key, s => s.Value);
        return rest.Count == 0 ? [] : JsonSerializer.SerializeToUtf8Bytes(rest);
    }

    private static byte[] VmConfiguration(IReadOnlyDictionary<string, string>? settings) =>
        settings is not null && settings.TryGetValue(VmConfigurationSetting, out var value) ? Encoding.UTF8.GetBytes(value) : [];
}
