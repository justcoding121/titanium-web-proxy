using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Lua.Native;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

internal static class LuaTestSupport
{
    private static readonly string ScriptDir = Path.Combine(Path.GetTempPath(), "twp-lua-tests");

    /// <summary>An empty <c>ctx</c> for calling a script function directly.</summary>
    public static readonly byte[] EmptySnapshot = [(byte)'T', 0, 0, 0, 0];

    /// <summary>Skips the calling test when the Lua shim is not beside the test assembly.</summary>
    public static void RequireNative()
    {
        if (!LuaNative.TryLoad(out var error))
        {
            Assert.Inconclusive(error);
        }
    }

    public static string Script(string source)
    {
        Directory.CreateDirectory(ScriptDir);
        var path = Path.Combine(ScriptDir, Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, source);
        return path;
    }

    public static PluginConfig Config(string source, int poolSize = 1, PluginLimitsConfig? limits = null, string? failMode = null,
        IReadOnlyDictionary<string, string>? settings = null, string? bodyMode = null, string name = "test",
        IReadOnlyList<string>? calloutHosts = null, int? timeoutMs = null) => new()
    {
        Name = name,
        Kind = PluginKinds.Lua,
        File = Script(source),
        FailMode = failMode,
        Settings = settings,
        BodyMode = bodyMode,
        CalloutHosts = calloutHosts,
        TimeoutMs = timeoutMs,
        Limits = limits ?? new PluginLimitsConfig { PoolSize = poolSize },
    };

    public static LuaPlugin Create(PluginConfig config, ILogger? logger = null)
    {
        RequireNative();
        var engine = new LuaPluginEngine();
        Assert.IsTrue(engine.TryCreate(config, logger, out var middleware, out var error), error);
        return (LuaPlugin)middleware!;
    }

    public static LuaPlugin Create(string source, int poolSize = 1, ILogger? logger = null) => Create(Config(source, poolSize), logger);

    public static string? CreateError(PluginConfig config)
    {
        RequireNative();
        var engine = new LuaPluginEngine();
        Assert.IsFalse(engine.TryCreate(config, null, out var middleware, out var error));
        Assert.IsNull(middleware);
        return error;
    }

    /// <summary>Calls a global function with an empty ctx and returns what it returned.</summary>
    public static async Task<object?> CallAsync(LuaPlugin plugin, string function) =>
        (await plugin.Runtime.CallAsync(function, EmptySnapshot, CancellationToken.None)).Return;

    public static async Task<string?> CallStringAsync(LuaPlugin plugin, string function) => LuaReader.AsString(await CallAsync(plugin, function));

    public static async Task<LuaScriptException> CallFailsAsync(LuaPlugin plugin, string function)
    {
        try
        {
            await plugin.Runtime.CallAsync(function, EmptySnapshot, CancellationToken.None);
        }
        catch (LuaScriptException ex)
        {
            return ex;
        }

        Assert.Fail($"{function} was expected to fail");
        return null!;
    }

    public static ScriptPhaseContext Phase(ScriptPhases phase, IDictionary<string, object?>? items = null,
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
        Items = items ?? new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
        ClientEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.1.2.3"), 5555),
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
