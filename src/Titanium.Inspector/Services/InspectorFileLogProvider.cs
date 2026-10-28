using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Titanium.Inspector.Localization;

namespace Titanium.Inspector.Services;

/// <summary>
/// Inspector-only dated file sink. Creates a new timestamped file on first write and whenever
/// the current file reaches the max size. Never overwrites or truncates an existing file.
/// </summary>
public sealed class InspectorFileLogProvider : ILoggerProvider
{
    private readonly Channel<RunLogEntry> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _configLock = new();
    private readonly Task _writerTask;

    private string _folder;
    private long _maxBytes;
    private int _keep;
    private string? _currentFilePath;
    private StreamWriter? _streamWriter;
    private long _currentSize;
    private long _retryAfterUtcTicks;
    private bool _permanentlyUnavailable;
    private long _droppedSinceMarker;

    public InspectorFileLogProvider(string folder, long maxBytes, int keep, int queueCapacity = 8192)
    {
        _folder = InspectorLogFiles.ResolveFolder(folder);
        _maxBytes = Math.Max(1024, maxBytes);
        _keep = Math.Clamp(keep, 1, 100);
        _channel = Channel.CreateBounded<RunLogEntry>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writerTask = Task.Run(WriteLoop, _stop.Token);
    }

    /// <summary>Path of the file currently being written, or null before the first line.</summary>
    public string? CurrentFilePath => Volatile.Read(ref _currentFilePath);

    public void Reconfigure(string folder, long maxBytes, int keep)
    {
        var resolved = InspectorLogFiles.ResolveFolder(folder);
        var bytes = Math.Max(1024, maxBytes);
        var keepCount = Math.Clamp(keep, 1, 100);
        lock (_configLock)
        {
            var folderChanged = !string.Equals(_folder, resolved, StringComparison.OrdinalIgnoreCase);
            _folder = resolved;
            _maxBytes = bytes;
            _keep = keepCount;
            _permanentlyUnavailable = false;
            _retryAfterUtcTicks = 0;
            if (folderChanged)
            {
                CloseWriterSilent();
                Volatile.Write(ref _currentFilePath, null);
                _currentSize = 0;
            }
        }

        InspectorLogFiles.Prune(resolved, keepCount, CurrentFilePath);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch
        {
            // best effort drain
        }

        CloseWriterSilent();
        _stop.Dispose();
    }

    private void Enqueue(RunLogEntry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            return;
        }

        Interlocked.Increment(ref _droppedSinceMarker);
    }

    private async Task WriteLoop()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    await WriteEntryAsync(entry).ConfigureAwait(false);
                }

                try
                {
                    lock (_configLock)
                    {
                        _streamWriter?.Flush();
                    }
                }
                catch (Exception)
                {
                    // flush best effort
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        // Drain remaining after cancel/complete
        while (_channel.Reader.TryRead(out var leftover))
        {
            try
            {
                await WriteEntryAsync(leftover).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }

        CloseWriterSilent();
    }

    private Task WriteEntryAsync(RunLogEntry entry)
    {
        if (_permanentlyUnavailable)
        {
            return Task.CompletedTask;
        }

        if (_retryAfterUtcTicks != 0 && DateTime.UtcNow.Ticks < _retryAfterUtcTicks)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Whole write under the config lock: Reconfigure() may close the writer from another
            // thread (proxy restart / settings change); an unlocked write could hit a disposed
            // StreamWriter.
            lock (_configLock)
            {
                WriteDroppedMarkerIfNeeded();
                EnsureWriter();
                var line = RunLogFormat.FormatLine(entry);
                _streamWriter!.WriteLine(line);
                _currentSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                _retryAfterUtcTicks = 0;

                if (_currentSize >= _maxBytes)
                {
                    RollToNewFile();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _permanentlyUnavailable = true;
            lock (_configLock)
            {
                CloseWriterSilent();
            }
        }
        catch (Exception)
        {
            // IOException, ObjectDisposedException, formatter faults, ...: one bad line must never
            // kill the writer loop (that silently stops all logging for the rest of the run).
            lock (_configLock)
            {
                CloseWriterSilent();
            }

            _retryAfterUtcTicks = DateTime.UtcNow.AddSeconds(1).Ticks;
        }

        return Task.CompletedTask;
    }

    private void WriteDroppedMarkerIfNeeded()
    {
        var dropped = Interlocked.Exchange(ref _droppedSinceMarker, 0);
        if (dropped <= 0)
        {
            return;
        }

        EnsureWriter();
        var marker = LanguageService.Format("logging.dropped", dropped);
        if (string.IsNullOrEmpty(marker) || marker.Contains("{0}", StringComparison.Ordinal)
            || string.Equals(marker, "logging.dropped", StringComparison.Ordinal))
        {
            marker = $"{dropped} log lines were dropped because logging could not keep up.";
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN ] Titanium.Inspector: {marker}";
        _streamWriter!.WriteLine(line);
        _currentSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
    }

    private void EnsureWriter()
    {
        if (_streamWriter is not null)
        {
            return;
        }

        string folder;
        int keep;
        lock (_configLock)
        {
            folder = _folder;
            keep = _keep;
        }

        var path = InspectorLogFiles.UniquePath(folder, DateTime.Now);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentSize = stream.Length;
        _streamWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        Volatile.Write(ref _currentFilePath, path);
        InspectorLogFiles.Prune(folder, keep, path);
    }

    private void RollToNewFile()
    {
        CloseWriterSilent();
        _currentSize = 0;
        Volatile.Write(ref _currentFilePath, null);
        // Next EnsureWriter creates a new dated file and prunes.
    }

    private void CloseWriterSilent()
    {
        try
        {
            _streamWriter?.Flush();
            _streamWriter?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // best effort
        }
        catch (IOException)
        {
            // best effort
        }
        finally
        {
            _streamWriter = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly InspectorFileLogProvider _provider;
        private readonly string _category;

        public FileLogger(InspectorFileLogProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            var message = RunLogFormat.BuildMessage(text, exception);
            _provider.Enqueue(new RunLogEntry(DateTime.Now, logLevel, _category, message));
        }
    }
}
