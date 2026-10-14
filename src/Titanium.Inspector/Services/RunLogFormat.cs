using System.Text;
using Microsoft.Extensions.Logging;

namespace Titanium.Inspector.Services;

/// <summary>
/// Shared line format for the disk log and Recent logs Copy text.
/// Matches Core <c>ProxyLog.FormatLine</c> layout.
/// </summary>
public static class RunLogFormat
{
    public static string FormatLine(RunLogEntry entry)
    {
        var levelText = LevelToToken(entry.Level).PadRight(5);
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{levelText}] {entry.Category}: {entry.Message}";
        return line;
    }

    public static string LevelToToken(LogLevel level) =>
        level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => level.ToString().ToUpperInvariant(),
        };

    public static string BuildMessage(string text, Exception? exception)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        if (exception is null)
        {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed);
        if (trimmed.Length > 0)
        {
            sb.Append(Environment.NewLine);
        }

        sb.Append(exception);
        return sb.ToString();
    }
}
