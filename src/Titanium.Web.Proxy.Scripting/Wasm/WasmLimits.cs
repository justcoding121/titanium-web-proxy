using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>The resolved limits of one WASM plugin, with the engine's defaults filled in and every value range-checked.</summary>
internal sealed record WasmLimits
{
    public const int DefaultMemoryMb = 64;
    public const int MaxMemoryMb = 4096;
    public const int DefaultCalloutConcurrency = 64;
    public const long DefaultBufferedBodyBytes = 4L * 1024 * 1024;
    public const long DefaultSharedDataBytes = 16L * 1024 * 1024;
    public const long DefaultQueueBytes = 4L * 1024 * 1024;

    public long MemoryBytes { get; init; } = DefaultMemoryMb * 1024L * 1024;

    public long? Fuel { get; init; }

    public int PoolSize { get; init; } = Environment.ProcessorCount;

    public int CalloutConcurrency { get; init; } = DefaultCalloutConcurrency;

    public long BufferedBodyBytes { get; init; } = DefaultBufferedBodyBytes;

    public long SharedDataBytes { get; init; } = DefaultSharedDataBytes;

    public long QueueBytes { get; init; } = DefaultQueueBytes;

    /// <summary>Longest one guest call may run before its epoch deadline traps it.</summary>
    public int EpochDeadlineMs { get; init; } = ProxyPluginContract.DefaultTimeoutMilliseconds;

    /// <summary>Longest a paused request waits for the guest to resume it.</summary>
    public int PauseTimeoutMs { get; init; } = ProxyPluginContract.MaxTimeoutMilliseconds;

    public static bool TryParse(PluginConfig config, out WasmLimits limits, out string? error)
    {
        var raw = config.Limits;
        limits = new WasmLimits();
        error = null;
        var timeout = config.TimeoutMs ?? ProxyPluginContract.DefaultTimeoutMilliseconds;

        if (raw?.MemoryMb is int memory && memory is < 1 or > MaxMemoryMb)
        {
            error = $"limits.memoryMb {memory} is outside 1 to {MaxMemoryMb}";
            return false;
        }

        if (raw?.Fuel is long fuel && fuel < 1)
        {
            error = $"limits.fuel {fuel} must be positive";
            return false;
        }

        if (raw?.PoolSize is int pool && pool is < 1 or > 1024)
        {
            error = $"limits.poolSize {pool} is outside 1 to 1024";
            return false;
        }

        if (raw?.EpochDeadlineMs is int epoch && epoch is < 1 or > ProxyPluginContract.MaxTimeoutMilliseconds)
        {
            error = $"limits.epochDeadlineMs {epoch} is outside 1 to {ProxyPluginContract.MaxTimeoutMilliseconds}";
            return false;
        }

        if (raw?.CalloutConcurrency is int callouts && callouts is < 0 or > 10_000)
        {
            error = $"limits.calloutConcurrency {callouts} is outside 0 to 10000";
            return false;
        }

        limits = new WasmLimits
        {
            MemoryBytes = (raw?.MemoryMb ?? DefaultMemoryMb) * 1024L * 1024,
            Fuel = raw?.Fuel,
            PoolSize = raw?.PoolSize ?? Math.Max(1, Environment.ProcessorCount),
            CalloutConcurrency = raw?.CalloutConcurrency ?? DefaultCalloutConcurrency,
            BufferedBodyBytes = raw?.BufferedBodyBytes is long body and > 0 ? body : DefaultBufferedBodyBytes,
            SharedDataBytes = raw?.SharedDataBytes is long shared and > 0 ? shared : DefaultSharedDataBytes,
            QueueBytes = raw?.QueueBytes is long queue and > 0 ? queue : DefaultQueueBytes,
            EpochDeadlineMs = raw?.EpochDeadlineMs ?? Math.Clamp(timeout, 1, ProxyPluginContract.MaxTimeoutMilliseconds),
        };
        return true;
    }
}
