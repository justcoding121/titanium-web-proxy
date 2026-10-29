using System.Globalization;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>Resolved limits and settings of one <c>kind: lua</c> plugin.</summary>
internal sealed class LuaOptions
{
    public const int DefaultMemoryMb = 64;
    public const long DefaultMaxInstructions = 10_000_000;
    public const int DefaultCalloutConcurrency = 16;
    public const long DefaultBufferedBodyBytes = 1024 * 1024;
    public const long DefaultQueueBytes = 4 * 1024 * 1024;
    public const long DefaultSharedDictBytes = 16 * 1024 * 1024;
    public const int DefaultCalloutTimeoutMs = 5000;

    /// <summary>Setting naming shared dictionaries to size up front: <c>name[:maxItems]</c>, comma separated.</summary>
    public const string SharedDictsSetting = "sharedDicts";

    /// <summary>Setting holding a JWKS document (<c>{"keys":[...]}</c>) for <c>jwt.verify</c>.</summary>
    public const string JwksSetting = "jwks";

    public required string Name { get; init; }

    public required string File { get; init; }

    public required byte[] Source { get; init; }

    public long MemoryBytes { get; init; } = DefaultMemoryMb * 1024L * 1024;

    /// <summary>VM instructions one call may run, across all of its segments.</summary>
    public long MaxInstructions { get; init; } = DefaultMaxInstructions;

    /// <summary>CPU time one call may spend in Lua, across all of its segments. Shorter than <see cref="Timeout"/>.</summary>
    public TimeSpan EngineDeadline { get; init; }

    /// <summary>Wall time of one call including callouts and sleeps; the backstop behind <see cref="EngineDeadline"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(ProxyPluginContract.DefaultTimeoutMilliseconds);

    public int PoolSize { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    public int CalloutConcurrency { get; init; } = DefaultCalloutConcurrency;

    public long BufferedBodyBytes { get; init; } = DefaultBufferedBodyBytes;

    /// <summary>Largest command buffer one segment may return (headers, bodies, logs it emitted).</summary>
    public long OutputBytes { get; init; } = DefaultQueueBytes;

    public long SharedDictBytes { get; init; } = DefaultSharedDictBytes;

    public IReadOnlyList<(string Name, long MaxItems)> SharedDicts { get; init; } = [];

    public bool FailOpen { get; init; }

    public ScriptBodyMode BodyMode { get; init; }

    public IReadOnlyList<string> CalloutHosts { get; init; } = [];

    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public static bool TryParse(PluginConfig config, out LuaOptions? options, out string? error)
    {
        options = null;
        var name = string.IsNullOrWhiteSpace(config.Name) ? "lua" : config.Name;
        if (string.IsNullOrWhiteSpace(config.File))
        {
            error = "no file is set";
            return false;
        }

        var path = Path.GetFullPath(config.File);
        if (!System.IO.File.Exists(path))
        {
            error = $"'{config.File}' does not exist";
            return false;
        }

        var limits = config.Limits ?? new PluginLimitsConfig();
        if (!InRange(limits.MemoryMb, 1, 4096, "limits.memoryMb", out error) ||
            !InRange(limits.MaxInstructions, 1000, 100_000_000_000, "limits.maxInstructions", out error) ||
            !InRange(limits.PoolSize, 1, 256, "limits.poolSize", out error) ||
            !InRange(limits.CalloutConcurrency, 1, 4096, "limits.calloutConcurrency", out error) ||
            !InRange(limits.BufferedBodyBytes, 1024, 1L << 30, "limits.bufferedBodyBytes", out error) ||
            !InRange(limits.QueueBytes, 4096, 1L << 30, "limits.queueBytes", out error) ||
            !InRange(limits.SharedDataBytes, 4096, 1L << 32, "limits.sharedDataBytes", out error) ||
            !InRange(limits.EpochDeadlineMs, 1, ProxyPluginContract.MaxTimeoutMilliseconds, "limits.epochDeadlineMs", out error))
        {
            return false;
        }

        if (limits.Fuel is not null)
        {
            error = "limits.fuel applies to wasm plugins; use limits.maxInstructions for lua";
            return false;
        }

        ScriptBodyMode bodyMode;
        if (string.IsNullOrWhiteSpace(config.BodyMode) || string.Equals(config.BodyMode, "streaming", StringComparison.OrdinalIgnoreCase))
        {
            bodyMode = ScriptBodyMode.Streaming;
        }
        else if (string.Equals(config.BodyMode, "buffered", StringComparison.OrdinalIgnoreCase))
        {
            bodyMode = ScriptBodyMode.Buffered;
        }
        else
        {
            error = $"bodyMode '{config.BodyMode}' is not 'streaming' or 'buffered'";
            return false;
        }

        var timeoutMs = config.TimeoutMs ?? ProxyPluginContract.DefaultTimeoutMilliseconds;
        var deadlineMs = limits.EpochDeadlineMs ?? Math.Max(1, timeoutMs * 4 / 5);
        if (deadlineMs >= timeoutMs && timeoutMs > 1)
        {
            error = $"limits.epochDeadlineMs ({deadlineMs}) must be shorter than timeoutMs ({timeoutMs}) so the engine stops the script first";
            return false;
        }

        if (!TryParseDicts(config.Settings, out var dicts, out error))
        {
            return false;
        }

        byte[] source;
        try
        {
            source = System.IO.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"'{config.File}' could not be read: {ex.Message}";
            return false;
        }

        options = new LuaOptions
        {
            Name = name,
            File = path,
            Source = source,
            MemoryBytes = (limits.MemoryMb ?? DefaultMemoryMb) * 1024L * 1024,
            MaxInstructions = limits.MaxInstructions ?? DefaultMaxInstructions,
            EngineDeadline = TimeSpan.FromMilliseconds(deadlineMs),
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            PoolSize = limits.PoolSize ?? Math.Clamp(Environment.ProcessorCount, 1, 8),
            CalloutConcurrency = limits.CalloutConcurrency ?? DefaultCalloutConcurrency,
            BufferedBodyBytes = limits.BufferedBodyBytes ?? DefaultBufferedBodyBytes,
            OutputBytes = limits.QueueBytes ?? DefaultQueueBytes,
            SharedDictBytes = limits.SharedDataBytes ?? DefaultSharedDictBytes,
            SharedDicts = dicts,
            FailOpen = string.Equals(config.FailMode, ProxyPluginContract.FailOpen, StringComparison.OrdinalIgnoreCase),
            BodyMode = bodyMode,
            CalloutHosts = config.CalloutHosts ?? [],
            Settings = config.Settings ?? new Dictionary<string, string>(),
        };
        error = null;
        return true;
    }

    private static bool TryParseDicts(IReadOnlyDictionary<string, string>? settings, out IReadOnlyList<(string, long)> dicts,
        out string? error)
    {
        var list = new List<(string, long)>();
        dicts = list;
        error = null;
        if (settings is null || !settings.TryGetValue(SharedDictsSetting, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            long items = 10_000;
            if (parts.Length == 2 &&
                (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out items) || items < 1 || items > 10_000_000))
            {
                error = $"settings.{SharedDictsSetting} entry '{entry}' needs a max item count of 1 to 10000000";
                return false;
            }

            if (parts[0].Length is 0 or > 64 || parts[0].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            {
                error = $"settings.{SharedDictsSetting} name '{parts[0]}' must be 1 to 64 letters, digits, '_', '-', or '.'";
                return false;
            }

            list.Add((parts[0], items));
        }

        return true;
    }

    private static bool InRange(long? value, long min, long max, string name, out string? error)
    {
        if (value is { } v && (v < min || v > max))
        {
            error = $"{name} must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        error = null;
        return true;
    }
}
