using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Central configuration for the proxy's built-in diagnostic logging. A single instance is owned by
///     <see cref="ProxyServer.Logging" />; mutate its properties (or replace the whole object) before
///     <see cref="ProxyServer.Start" /> to control how the proxy reports every caught exception and
///     diagnostic event. Logging never blocks proxy traffic: built-in sinks are asynchronous and
///     best-effort, and setting <see cref="Enabled" /> to <see langword="false" /> removes all logging
///     overhead (no timestamps are read, no strings are formatted, no providers run).
/// </summary>
public sealed class ProxyLoggingOptions
{
    /// <summary>
    ///     Master switch for all proxy logging. When <see langword="false" /> (default is
    ///     <see langword="true" />) the proxy uses a no-op logger; no log-related work of any kind is
    ///     performed anywhere in the library, so this is the zero-overhead configuration for users who
    ///     do not want logging at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     The minimum <see cref="LogLevel" /> that is actually written to any sink. Every caught
    ///     exception in the proxy is still reported to the gateway regardless of this setting - this
    ///     only controls how much of that stream is materialized/written. Defaults to
    ///     <see cref="LogLevel.Error" /> so out-of-the-box behavior stays quiet, while still surfacing
    ///     every genuinely unexpected failure.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Error;

    /// <summary>
    ///     Whether the built-in console sink is active. Defaults to <see langword="true" />. Ignored when
    ///     <see cref="LoggerFactory" /> is set.
    /// </summary>
    public bool EnableConsole { get; set; } = true;

    /// <summary>
    ///     Whether the built-in rolling-file sink is active. Defaults to <see langword="false" />.
    ///     Ignored when <see cref="LoggerFactory" /> is set.
    /// </summary>
    public bool EnableFile { get; set; }

    /// <summary>
    ///     Path of the log file used by the built-in rolling-file sink. Relative paths are resolved
    ///     against the current working directory. The containing directory is created on demand.
    /// </summary>
    public string FilePath { get; set; } = "logs/titanium-proxy.log";

    /// <summary>
    ///     Maximum size, in bytes, a log file is allowed to reach before it is rolled. Defaults to 10 MiB.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    ///     Maximum number of rolled-over log files retained alongside the active log file. Defaults to 5.
    /// </summary>
    public int MaxRolledFiles { get; set; } = 5;

    /// <summary>
    ///     Capacity of the bounded in-memory queue used by each built-in sink before entries are
    ///     considered saturated (see the sink's own delivery guarantees). Defaults to 4096.
    /// </summary>
    public int QueueCapacity { get; set; } = 4096;

    /// <summary>
    ///     Optional externally supplied <see cref="ILoggerFactory" /> (e.g. bridging to Serilog, NLog, or
    ///     an ASP.NET Core host's logging pipeline). When set, the built-in Console/File sinks are not
    ///     created and this factory is used verbatim; the proxy never disposes a factory it does not own.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
