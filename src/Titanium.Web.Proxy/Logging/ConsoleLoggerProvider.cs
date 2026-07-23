using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Built-in console sink. Writes <see cref="LogLevel.Warning" /> and above to <c>stderr</c> and
///     everything else to <c>stdout</c>, matching common console-logging conventions. All writes happen
///     on the single background writer thread owned by <see cref="ChannelLoggerProviderBase" />.
/// </summary>
internal sealed class ConsoleLoggerProvider : ChannelLoggerProviderBase
{
    public ConsoleLoggerProvider(ProxyLoggingOptions options) : base(options.QueueCapacity)
    {
    }

    protected override Task WriteEntryAsync(LogEntry entry)
    {
        WriteEntrySync(entry);
        return Task.CompletedTask;
    }

    protected override void WriteEntrySync(LogEntry entry)
    {
        var line = ProxyLog.FormatLine(entry);
        var writer = entry.Level >= LogLevel.Warning ? Console.Error : Console.Out;

        // A single Console.Out/Error write call is used (rather than separate WriteLine calls for the
        // message and exception) so interleaved log lines from concurrent sessions cannot split a
        // single entry across two unrelated lines.
        writer.WriteLine(line);
    }
}
