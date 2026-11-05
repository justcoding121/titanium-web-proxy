using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;

namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>The <c>plugins[].kind</c> values and their normal form.</summary>
public static class PluginKinds
{
    /// <summary>A .NET assembly that holds an <see cref="IProxyMiddleware"/> class. The default.</summary>
    public const string DotNet = "dotnet";

    /// <summary>A Lua 5.4 script run by a registered <see cref="IPluginEngine"/>.</summary>
    public const string Lua = "lua";

    /// <summary>A WebAssembly module run by a registered <see cref="IPluginEngine"/>.</summary>
    public const string Wasm = "wasm";

    /// <summary>Returns the lower-case kind, with null, empty, and white space meaning <see cref="DotNet"/>.</summary>
    public static string Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? DotNet : kind.Trim().ToLowerInvariant();

    /// <summary>True for a kind the configuration accepts: <see cref="DotNet"/>, <see cref="Lua"/>, or <see cref="Wasm"/>.</summary>
    public static bool IsKnown(string? kind) => Normalize(kind) is DotNet or Lua or Wasm;

    /// <summary>True for a kind that is run by an <see cref="IPluginEngine"/> instead of the .NET loader.</summary>
    public static bool IsScript(string? kind) => Normalize(kind) is Lua or Wasm;
}

/// <summary>
/// Builds the middleware for one script kind. The core never references an engine's own types: an engine package implements
/// this interface and registers itself in <see cref="PluginEngines"/>, and the plugin host asks the registry by kind.
/// </summary>
public interface IPluginEngine
{
    /// <summary>The <see cref="PluginKinds"/> value this engine serves, for example <c>lua</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Builds the middleware for <paramref name="config"/>. Returns false and sets <paramref name="error"/> to the reason when
    /// it cannot (a missing file, a script that does not compile, a limit out of range). A middleware that implements
    /// <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> is disposed when its configuration generation retires.
    /// </summary>
    bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error);
}

/// <summary>
/// Process-wide registry of <see cref="IPluginEngine"/> by kind. An engine package fills it at startup; registration can be
/// lazy, so an engine and its native library load only when a configuration names its kind.
/// </summary>
public static class PluginEngines
{
    private static readonly ConcurrentDictionary<string, Lazy<IPluginEngine>> Engines = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="engine"/> for its <see cref="IPluginEngine.Kind"/>, replacing an earlier one.</summary>
    public static void Register(IPluginEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Register(engine.Kind, () => engine);
    }

    /// <summary>
    /// Registers a factory for <paramref name="kind"/>, replacing an earlier registration. The factory runs once, the first
    /// time a plugin of that kind loads.
    /// </summary>
    public static void Register(string kind, Func<IPluginEngine> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var key = PluginKinds.Normalize(kind);
        if (key == PluginKinds.DotNet || !PluginKinds.IsKnown(key))
        {
            throw new ArgumentException($"'{kind}' is not a script plugin kind ({PluginKinds.Lua} or {PluginKinds.Wasm}).", nameof(kind));
        }

        Engines[key] = new Lazy<IPluginEngine>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Removes the registration for <paramref name="kind"/>. Returns false when there was none.</summary>
    public static bool Unregister(string kind) => Engines.TryRemove(PluginKinds.Normalize(kind), out _);

    /// <summary>True when an engine or a factory is registered for <paramref name="kind"/>. Does not run the factory.</summary>
    public static bool IsRegistered(string kind) => Engines.ContainsKey(PluginKinds.Normalize(kind));

    /// <summary>Finds the engine for <paramref name="kind"/>, creating it on first use.</summary>
    public static bool TryGet(string kind, out IPluginEngine? engine)
    {
        if (Engines.TryGetValue(PluginKinds.Normalize(kind), out var lazy))
        {
            engine = lazy.Value;
            return true;
        }

        engine = null;
        return false;
    }
}