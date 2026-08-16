using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Built-in console sink. Writes <see cref="LogLevel.Warning" /> and above to <c>stderr</c> and
///     everything else to <c>stdout</c>, matching common console-logging conventions. All writes happen
///     on the single background writer thread owned by <see cref="ChannelLoggerProviderBase" />.
///     Optionally colors each line by level (see <see cref="ProxyLoggingOptions.EnableConsoleColors" />).
/// </summary>
internal sealed class ConsoleLoggerProvider : ChannelLoggerProviderBase
{
    private const string AnsiReset = "\x1b[0m";

    private readonly bool colorizeStdout;
    private readonly bool colorizeStderr;

    public ConsoleLoggerProvider(ProxyLoggingOptions options) : base(options.QueueCapacity)
    {
        // Redirected output (piped to a file, captured by a test runner, etc.) must never receive raw
        // escape codes, so each stream is gated on its own redirection state - checked once here since
        // it cannot change for the lifetime of the process.
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        colorizeStdout = ShouldColorize(options.EnableConsoleColors, Console.IsOutputRedirected, noColor);
        colorizeStderr = ShouldColorize(options.EnableConsoleColors, Console.IsErrorRedirected, noColor);
    }

    /// <summary>
    ///     Whether a single console stream should receive ANSI color codes. Factored out as a pure,
    ///     testable function of its inputs rather than reading <see cref="Console.IsOutputRedirected" />
    ///     etc. directly in a constructor that also needs to run under test.
    /// </summary>
    internal static bool ShouldColorize(bool enableConsoleColors, bool streamIsRedirected, string? noColorEnvValue)
    {
        // NO_COLOR (https://no-color.org/) is a cross-tool convention: any non-empty value means "never
        // emit color", independent of our own EnableConsoleColors switch.
        return enableConsoleColors && !streamIsRedirected && string.IsNullOrEmpty(noColorEnvValue);
    }

    protected override async Task WriteEntryAsync(LogEntry entry)
    {
        var line = ProxyLog.FormatLine(entry);
        var toStderr = entry.Level >= LogLevel.Warning;
        var writer = toStderr ? Console.Error : Console.Out;
        var colorize = toStderr ? colorizeStderr : colorizeStdout;
        var text = colorize ? Colorize(entry.Level, line) : line;
        await writer.WriteLineAsync(text).ConfigureAwait(false);
    }

    internal static string Colorize(LogLevel level, string line)
    {
        var color = AnsiColorFor(level);
        return color == null ? line : color + line + AnsiReset;
    }

    internal static string? AnsiColorFor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "\x1b[90m", // dark gray
            LogLevel.Debug => "\x1b[90m", // dark gray
            LogLevel.Information => null, // default terminal color
            LogLevel.Warning => "\x1b[33m", // yellow
            LogLevel.Error => "\x1b[31m", // red
            LogLevel.Critical => "\x1b[1m\x1b[31m", // bold red
            _ => null
        };
    }
}
