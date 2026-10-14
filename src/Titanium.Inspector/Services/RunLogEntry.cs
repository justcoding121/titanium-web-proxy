using Microsoft.Extensions.Logging;

namespace Titanium.Inspector.Services;

/// <summary>One line kept in Recent logs (and formatted the same way as the disk log).</summary>
public sealed record RunLogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelToken => RunLogFormat.LevelToToken(Level);

    public bool IsError => Level is LogLevel.Error or LogLevel.Critical;

    public bool IsWarn => Level == LogLevel.Warning;

    public bool IsMuted => Level is LogLevel.Debug or LogLevel.Trace;

    public bool IsCritical => Level == LogLevel.Critical;
}
