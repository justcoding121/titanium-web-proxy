using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

internal static class WasmTestSupport
{
    /// <summary>Tests that go through the public engine constructor must not write to the user's module cache.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void DisableDefaultModuleCache() => Environment.SetEnvironmentVariable(WasmModuleCache.DirectoryVariable, "off");

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Wasm", "Samples", name);

    /// <summary>Skips the calling test when the Wasmtime native is not beside the test assembly.</summary>
    public static void RequireNative()
    {
        if (!WasmRuntime.TryLoadNative(out var error))
        {
            Assert.Inconclusive(error);
        }
    }

    public static PluginConfig Config(string sample, int poolSize = 1, PluginLimitsConfig? limits = null, string? failMode = null,
        IReadOnlyDictionary<string, string>? settings = null, string? bodyMode = null, string name = "test", string? vmId = null,
        IReadOnlyList<string>? calloutHosts = null) => new()
    {
        Name = name,
        Kind = PluginKinds.Wasm,
        File = sample.Contains(Path.DirectorySeparatorChar) ? sample : Sample(sample),
        FailMode = failMode,
        Settings = settings,
        BodyMode = bodyMode,
        VmId = vmId,
        CalloutHosts = calloutHosts,
        Limits = limits ?? new PluginLimitsConfig { PoolSize = poolSize },
    };

    /// <summary>Builds a plugin with no module cache (or <paramref name="cache"/>), so tests never touch the user's cache.</summary>
    public static WasmPlugin Create(PluginConfig config, ILogger? logger = null, WasmModuleCache? cache = null)
    {
        RequireNative();
        var engine = new WasmPluginEngine(cache);
        Assert.IsTrue(engine.TryCreate(config, logger, out var middleware, out var error), error);
        return (WasmPlugin)middleware!;
    }

    public static string? CreateError(PluginConfig config)
    {
        RequireNative();
        var engine = new WasmPluginEngine(null);
        Assert.IsFalse(engine.TryCreate(config, null, out var middleware, out var error));
        Assert.IsNull(middleware);
        return error;
    }

    public static ScriptPhaseContext Phase(ScriptPhases phase, IDictionary<string, object?> items,
        IEnumerable<KeyValuePair<string, string>>? headers = null, string method = "GET", string path = "/foo?x=1",
        int status = 0, byte[]? chunk = null, bool last = false) => new()
    {
        Phase = phase,
        Method = method,
        Path = path,
        Host = "example.test",
        StatusCode = status,
        Headers = (headers ?? []).ToArray(),
        Chunk = chunk ?? ReadOnlyMemory<byte>.Empty,
        IsLastChunk = last,
        Items = items,
        RouteId = "r1",
        ClusterId = "c1",
    };

    public static KeyValuePair<string, string> H(string name, string value) => new(name, value);

    public static async Task WaitForAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(condition(), "condition was not met in time");
    }
}

/// <summary>Collects log lines so a test can see what a guest logged.</summary>
internal sealed class ListLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}
