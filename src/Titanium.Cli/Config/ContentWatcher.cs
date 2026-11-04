using System.Security.Cryptography;

namespace Titanium.Cli.Config;

/// <summary>
///     Calls back when the <em>content</em> of a set of files changes, including when a Kubernetes ConfigMap or
///     Secret mount swaps its <c>..data</c> symlink. A plain <c>FileSystemWatcher</c> on the file name misses that
///     swap: the mounted file name never changes, only the symlink it resolves through.
///     <list type="bullet">
///         <item>
///             It watches each file's containing directory (and the directory of a resolved link target) for
///             Created, Changed, and Renamed events on the file, on <c>..data</c>, on any <c>..</c>-prefixed
///             entry, and on the resolved target.
///         </item>
///         <item>
///             Every event, and a <see cref="DefaultPollInterval" /> poll for the case where events are lost,
///             restarts a <see cref="DefaultDebounce" /> timer. When it fires, the files are hashed and the callback
///             runs only if the hash differs from the last one seen. One swap is one callback however many events it
///             raised.
///         </item>
///         <item>
///             A file that is missing or locked mid-swap defers the decision instead of reporting a change.
///             A callback that throws is reported through <c>onError</c> and the watcher keeps going; the failed
///             content is not retried until the files change again.
///         </item>
///     </list>
///     Nothing here touches the proxy request path.
/// </summary>
internal sealed class ContentWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly string[] _paths;
    private readonly Func<Task> _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly bool _useEvents;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer? _poll;
    private CancellationTokenSource? _pending;
    private string[] _resolvedTargets = [];
    private string? _last;
    private bool _disposed;

    /// <param name="paths">Files to watch. Their current content is the baseline: no callback for it.</param>
    /// <param name="onChanged">Runs once per content change, on a pool thread, one at a time.</param>
    /// <param name="onError">Receives an exception thrown by <paramref name="onChanged" />.</param>
    /// <param name="debounce">Quiet time after the last event before the files are hashed. Default 250 ms.</param>
    /// <param name="pollInterval">Content-hash poll that backs up lost events. Default 30 s.</param>
    /// <param name="timeProvider">Clock for the debounce and poll timers.</param>
    /// <param name="useFileSystemEvents">False leaves only the poll (tests, and file systems without change events).</param>
    public ContentWatcher(
        IEnumerable<string> paths,
        Func<Task> onChanged,
        Action<Exception>? onError = null,
        TimeSpan? debounce = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null,
        bool useFileSystemEvents = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(onChanged);
        _paths = paths.Select(static p => Path.GetFullPath(p)).Distinct(StringComparer.Ordinal).ToArray();
        _onChanged = onChanged;
        _onError = onError;
        _time = timeProvider ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _useEvents = useFileSystemEvents;

        _last = Fingerprint();
        if (_useEvents)
        {
            RefreshWatchers();
        }

        var interval = pollInterval ?? DefaultPollInterval;
        _poll = interval > TimeSpan.Zero && interval != Timeout.InfiniteTimeSpan
            ? _time.CreateTimer(static state => ((ContentWatcher)state!).Trigger(), this, interval, interval)
            : null;
    }

    /// <summary>Paths being watched, absolute.</summary>
    public IReadOnlyList<string> Paths => _paths;

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
            _pending = null;
        }

        _poll?.Dispose();
        _stop.Cancel();
        pending?.Cancel();
        pending?.Dispose();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _stop.Dispose();
    }

    /// <summary>Restarts the debounce. Called for every relevant event and for each poll tick.</summary>
    internal void Trigger()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var previous = _pending;
            previous?.Cancel();
            previous?.Dispose();
            var next = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _pending = next;
            token = next.Token;
        }

        _ = Task.Run(() => DebouncedCheckAsync(token), CancellationToken.None);
    }

    private async Task DebouncedCheckAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, _time, token).ConfigureAwait(false);
            await CheckAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer event restarted the debounce, or the watcher was disposed.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a check was in flight.
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        await _checkLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = Fingerprint();
            if (now is null || string.Equals(now, _last, StringComparison.Ordinal))
            {
                return;
            }

            _last = now;
            if (_useEvents)
            {
                RefreshWatchers();
            }

            try
            {
                await _onChanged().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _onError?.Invoke(ex);
            }
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>SHA-256 over every path and its bytes. Null while any file is missing or cannot be read.</summary>
    private string? Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        foreach (var path in _paths)
        {
            try
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
                hash.AppendData([0]);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void RefreshWatchers()
    {
        var targets = new List<string>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _paths)
        {
            AddDirectory(directories, Path.GetDirectoryName(path));
            var target = TryResolveTarget(path);
            if (target is null)
            {
                continue;
            }

            targets.Add(target);
            // A directory reached through ..data is a symlink that the next swap replaces; the parent watch sees that.
            if (!HasKubernetesSegment(target))
            {
                AddDirectory(directories, Path.GetDirectoryName(target));
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _resolvedTargets = targets.ToArray();
            foreach (var directory in directories)
            {
                if (_watchers.ContainsKey(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                                       | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false,
                    };
                    watcher.Changed += OnEvent;
                    watcher.Created += OnEvent;
                    watcher.Renamed += OnEvent;
                    watcher.EnableRaisingEvents = true;
                    _watchers[directory] = watcher;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // No change events here (inotify limit, unsupported file system). The poll still covers it.
                }
            }
        }
    }

    private static void AddDirectory(HashSet<string> directories, string? directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            directories.Add(directory);
        }
    }

    private static string? TryResolveTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True for a path with a <c>..data</c> or <c>..2026_…</c> segment, the names a Kubernetes volume swaps.</summary>
    internal static bool HasKubernetesSegment(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar)
            .Any(static segment => segment.Length > 2 && segment.StartsWith("..", StringComparison.Ordinal));

    private void OnEvent(object? sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.Name, e.FullPath))
        {
            Trigger();
        }
    }

    private bool IsRelevant(string? name, string fullPath)
    {
        if (!string.IsNullOrEmpty(name))
        {
            // A rename reports the new name relative to the watched directory.
            var leaf = Path.GetFileName(name);
            if (string.Equals(leaf, "..data", StringComparison.Ordinal) ||
                leaf.StartsWith("..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        var full = Path.GetFullPath(fullPath);
        string[] targets;
        lock (_gate)
        {
            targets = _resolvedTargets;
        }

        if (_paths.Any(path => string.Equals(path, full, GetPathComparison())))
        {
            return true;
        }

        return targets.Any(target => string.Equals(target, full, GetPathComparison()));
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
