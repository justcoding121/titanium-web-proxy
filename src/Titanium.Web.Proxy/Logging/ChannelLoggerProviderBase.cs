using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     Shared plumbing for the built-in asynchronous sinks (<see cref="ConsoleLoggerProvider" />,
///     <see cref="RollingFileLoggerProvider" />). Every log call goes through a bounded
///     <see cref="Channel{T}" /> and a single dedicated writer task/thread per sink, so a call site
///     (including deep inside a hot proxy code path) never blocks on console/disk I/O: it only does a
///     non-blocking <see cref="ChannelWriter{T}.TryWrite" />.
///     Delivery is best-effort: when the queue saturates, low-severity entries are dropped (and counted)
///     while <see cref="LogLevel.Error" />/<see cref="LogLevel.Critical" /> entries fall back to a
///     guarded synchronous write so genuinely important failures are not silently lost.
/// </summary>
internal abstract class ChannelLoggerProviderBase : ILoggerProvider
{
    private readonly Channel<LogEntry> channel;
    private readonly Task writerTask;
    private readonly CancellationTokenSource stopTokenSource = new();
    private long droppedEntries;
    private bool disposed;

    protected ChannelLoggerProviderBase(int queueCapacity)
    {
        channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        writerTask = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ChannelSinkLogger(this, categoryName);
    }

    internal void Enqueue(in LogEntry entry)
    {
        if (disposed) return;

        if (channel.Writer.TryWrite(entry)) return;

        Interlocked.Increment(ref droppedEntries);

        // The async queue is saturated. Errors/critical failures are important enough to justify a
        // synchronous, best-effort write here rather than being silently dropped like lower-severity
        // entries; this keeps the common case (queue not saturated) fully asynchronous.
        if (entry.Level >= LogLevel.Error)
            try
            {
                WriteEntrySync(entry);
            }
            catch
            {
                // A logging sink must never throw into proxy code.
            }
    }

    /// <summary>
    ///     Writes one formatted entry to the underlying sink (console/file). Called only from the single
    ///     background writer task, so implementations do not need to be thread-safe against each other.
    /// </summary>
    protected abstract Task WriteEntryAsync(LogEntry entry);

    /// <summary>
    ///     A synchronous fallback write used only when the async queue is saturated and the entry is
    ///     high-severity. Default implementation blocks on <see cref="WriteEntryAsync" />; sinks may
    ///     override with a cheaper synchronous path.
    /// </summary>
    protected virtual void WriteEntrySync(LogEntry entry)
    {
        WriteEntryAsync(entry).GetAwaiter().GetResult();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                while (channel.Reader.TryRead(out var entry))
                    try
                    {
                        await WriteEntryAsync(entry).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A misbehaving sink (disk full, permission error, etc.) must never crash the
                        // shared writer loop or escape into proxy code; best-effort delivery only.
                    }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        channel.Writer.TryComplete();

        try
        {
            // Bounded drain: give the writer a short window to flush what is already queued, but never
            // block shutdown indefinitely on a stuck sink.
            writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore - best-effort drain only
        }

        stopTokenSource.Dispose();
        DisposeSink();
    }

    /// <summary>
    ///     Releases sink-specific resources (open file handles, etc.). Called once, after the writer
    ///     queue has been drained (or the drain timeout elapsed).
    /// </summary>
    protected virtual void DisposeSink()
    {
    }

    private sealed class ChannelSinkLogger : ILogger
    {
        private readonly ChannelLoggerProviderBase provider;
        private readonly string category;

        public ChannelSinkLogger(ChannelLoggerProviderBase provider, string category)
        {
            this.provider = provider;
            this.category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            provider.Enqueue(new LogEntry(DateTime.Now, logLevel, category, eventId, message, exception));
        }
    }
}
