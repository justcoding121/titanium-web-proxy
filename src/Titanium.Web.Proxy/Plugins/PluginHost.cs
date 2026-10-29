using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Web.Proxy.Plugins;

/// <summary>
/// Owns the per-route .NET plugins of one configuration: one collectible <see cref="AssemblyLoadContext"/> per plugin,
/// one instance of its class, and the isolation wrapper (exception, timeout, metrics) that routes call. A reload builds a
/// new host, swaps it in, and retires the old one, which disposes every plugin and unloads every context.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private const int Active = 0;
    private const int Retired = 1;
    private const int Disposed = 2;

    private readonly List<LoadedPlugin> _loaded;
    private readonly ILogger? _logger;
    private int _inFlight;
    private int _state;

    private PluginHost(List<LoadedPlugin> loaded, ILogger? logger)
    {
        _loaded = loaded;
        _logger = logger;
        Plugins = new Dictionary<string, IProxyMiddleware>(0, StringComparer.Ordinal);
    }

    /// <summary>The plugins by configured name, for <c>ReverseProxyOptions.RoutePlugins</c>.</summary>
    public IReadOnlyDictionary<string, IProxyMiddleware> Plugins { get; private set; }

    /// <summary>True once every plugin was disposed and every context unloaded.</summary>
    public bool IsDisposed => Volatile.Read(ref _state) == Disposed;

    /// <summary>Weak handles to the load contexts, so a test or an operator can confirm the runtime reclaimed them.</summary>
    public IReadOnlyList<WeakReference> LoadContexts => _loaded.Select(p => new WeakReference(p.Context)).ToArray();

    /// <summary>
    /// Loads every plugin. Nothing is kept when one fails: the contexts loaded so far are unloaded and the exception names
    /// the plugin and the reason.
    /// </summary>
    public static PluginHost Load(IEnumerable<PluginConfig>? configs, ILogger? logger = null)
    {
        var loaded = new List<LoadedPlugin>();
        if (configs is null)
        {
            return new PluginHost(loaded, logger);
        }

        var host = new PluginHost(loaded, logger);
        try
        {
            foreach (var config in configs)
            {
                loaded.Add(LoadOne(config, host));
            }
        }
        catch
        {
            host.Dispose();
            throw;
        }

        host.Plugins = host.BuildMap();
        return host;
    }

    /// <summary>Stops new work from being counted against this host and disposes it once the calls in flight finish.</summary>
    public void Retire()
    {
        Interlocked.CompareExchange(ref _state, Retired, Active);
        if (Volatile.Read(ref _inFlight) == 0)
        {
            Dispose();
        }
    }

    /// <summary>Disposes every plugin that is disposable and unloads every load context. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, Disposed) == Disposed)
        {
            return;
        }

        foreach (var plugin in _loaded)
        {
            DisposePlugin(plugin);
        }

        _loaded.Clear();
    }

    internal bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _state) == Disposed)
        {
            Exit();
            return false;
        }

        return true;
    }

    internal void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _state) == Retired)
        {
            Dispose();
        }
    }

    private IReadOnlyDictionary<string, IProxyMiddleware> BuildMap()
    {
        var map = new Dictionary<string, IProxyMiddleware>(_loaded.Count, StringComparer.Ordinal);
        foreach (var plugin in _loaded)
        {
            map[plugin.Name] = plugin.Wrapper;
        }

        return map;
    }

    private static LoadedPlugin LoadOne(PluginConfig config, PluginHost host)
    {
        var name = config.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("A plugin has no name.");
        }

        if (host._loaded.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Plugin '{name}' is defined twice.");
        }

        if (string.IsNullOrWhiteSpace(config.Assembly))
        {
            throw new InvalidOperationException($"Plugin '{name}' has no assembly.");
        }

        var path = System.IO.Path.GetFullPath(config.Assembly);
        if (!System.IO.File.Exists(path))
        {
            throw new InvalidOperationException($"Plugin '{name}': assembly '{config.Assembly}' does not exist.");
        }

        var failOpen = ParseFailMode(name, config.FailMode);
        var timeout = ParseTimeout(name, config.TimeoutMs);

        var context = new PluginLoadContext("plugin:" + name, path);
        try
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadPluginAssembly();
            }
            catch (Exception ex) when (ex is BadImageFormatException or System.IO.IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Plugin '{name}': assembly '{config.Assembly}' cannot be loaded: {ex.Message}", ex);
            }

            if (!PluginTypeResolver.TryResolve(assembly, config.TypeName, out var type, out var error))
            {
                throw new InvalidOperationException($"Plugin '{name}': {error}.");
            }

            IProxyMiddleware instance;
            try
            {
                instance = PluginTypeResolver.Create(type!, config.Settings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Plugin '{name}': {type!.FullName} failed to construct: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}", ex);
            }

            var wrapper = new RoutePluginMiddleware(name, instance, failOpen, timeout, host, host._logger);
            return new LoadedPlugin(name, context, instance, wrapper);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static bool ParseFailMode(string name, string? value)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, ProxyPluginContract.FailClosed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(value, ProxyPluginContract.FailOpen, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException($"Plugin '{name}': failMode '{value}' is not 'closed' or 'open'.");
    }

    private static TimeSpan ParseTimeout(string name, int? milliseconds)
    {
        var value = milliseconds ?? ProxyPluginContract.DefaultTimeoutMilliseconds;
        if (value is < 1 or > ProxyPluginContract.MaxTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"Plugin '{name}': timeoutMs {value} is outside 1 to {ProxyPluginContract.MaxTimeoutMilliseconds}.");
        }

        return TimeSpan.FromMilliseconds(value);
    }

    private void DisposePlugin(LoadedPlugin plugin)
    {
        try
        {
            switch (plugin.Instance)
            {
                case IAsyncDisposable asyncDisposable:
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Plugin '{Plugin}' threw while it was disposed.", plugin.Name);
        }

        plugin.Wrapper.Detach();
        plugin.Context.Unload();
    }

    private sealed record LoadedPlugin(
        string Name, PluginLoadContext Context, IProxyMiddleware Instance, RoutePluginMiddleware Wrapper);
}
