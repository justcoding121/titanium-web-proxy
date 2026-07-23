using System;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     A fully-formatted, structured log record produced by a <see cref="ProxyLogger" /> and consumed
///     asynchronously by a built-in sink (<see cref="ConsoleLoggerProvider" />/
///     <see cref="RollingFileLoggerProvider" />). The message is rendered once at the call site (cheaply,
///     and only when the level is enabled) so sinks never touch the original format string/state.
/// </summary>
internal readonly struct LogEntry
{
    public LogEntry(DateTime timestamp, LogLevel level, string category, EventId eventId, string message,
        Exception? exception)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category;
        EventId = eventId;
        Message = message;
        Exception = exception;
    }

    public DateTime Timestamp { get; }

    public LogLevel Level { get; }

    public string Category { get; }

    public EventId EventId { get; }

    public string Message { get; }

    public Exception? Exception { get; }
}
