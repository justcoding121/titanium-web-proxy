using System.Globalization;
using System.Runtime.InteropServices;

namespace Titanium.Cli.Config;

/// <summary>Process facts the hot-restart handover needs. Faked in tests; real on Linux only.</summary>
internal interface IHotRestartPlatform
{
    /// <summary>True when a process with <paramref name="pid" /> exists.</summary>
    bool IsProcessAlive(int pid);

    /// <summary>True when <paramref name="pid" /> runs the same program as this process (a recycled pid does not).</summary>
    bool IsSameProgram(int pid);

    /// <summary>Sends the handover signal (SIGUSR2). False when the signal could not be delivered.</summary>
    bool SignalHandover(int pid);
}

internal enum HotRestartHandover
{
    /// <summary>No pid file: this is the first process.</summary>
    NoPrevious,

    /// <summary>The pid file named a live process of the same program and it was signalled.</summary>
    SignaledPrevious,

    /// <summary>The pid file named a dead process or another program; it was ignored and overwritten.</summary>
    StalePidFile,

    /// <summary>The pid file already names this process.</summary>
    SamePid,

    /// <summary>The previous process is alive but the signal failed; it keeps serving and this process shares the ports.</summary>
    SignalFailed,
}

/// <summary>
///     Pid-file side of <c>server.hotRestart</c> (Linux). The new process binds with SO_REUSEPORT and becomes
///     ready first, then calls <see cref="TakeOver" />: it signals the old process with SIGUSR2 and writes its own pid.
///     The old process drains and exits on its own. This process never kills the old one. Two simultaneous restarts are
///     serialized by an exclusive lock on <c>&lt;pidFile&gt;.lock</c>.
/// </summary>
internal sealed class HotRestartCoordinator
{
    private readonly string _pidFile;
    private readonly IHotRestartPlatform _platform;
    private readonly int _ownPid;

    public HotRestartCoordinator(string pidFile, IHotRestartPlatform platform, int ownPid)
    {
        _pidFile = Path.GetFullPath(pidFile);
        _platform = platform;
        _ownPid = ownPid;
    }

    /// <summary>The pid that was signalled by <see cref="TakeOver" />, or null.</summary>
    public int? PreviousPid { get; private set; }

    /// <summary>Signals the previous process (when there is a live one) and records this process in the pid file.</summary>
    public HotRestartHandover TakeOver(TimeSpan lockWait)
    {
        using var gate = AcquireLock(lockWait);
        var previous = ReadPid();
        var outcome = HotRestartHandover.NoPrevious;
        if (previous is { } pid)
        {
            if (pid == _ownPid)
            {
                outcome = HotRestartHandover.SamePid;
            }
            else if (!_platform.IsProcessAlive(pid) || !_platform.IsSameProgram(pid))
            {
                outcome = HotRestartHandover.StalePidFile;
            }
            else if (_platform.SignalHandover(pid))
            {
                PreviousPid = pid;
                outcome = HotRestartHandover.SignaledPrevious;
            }
            else
            {
                outcome = HotRestartHandover.SignalFailed;
            }
        }

        // Even when the signal failed, the pid file names the newest process: the next restart signals this one.
        if (outcome != HotRestartHandover.SamePid)
        {
            WritePidAtomically();
        }

        return outcome;
    }

    /// <summary>
    ///     The pid of a live process of the same program named by the pid file, or null. Read-only (no lock, no
    ///     signal): used at startup to decide whether UDP and HTTP/3 binds must wait for that process to exit.
    /// </summary>
    public int? PeekLivePrevious()
    {
        var pid = ReadPid();
        return pid is { } other && other != _ownPid && _platform.IsProcessAlive(other) && _platform.IsSameProgram(other)
            ? other
            : null;
    }

    /// <summary>Deletes the pid file only when it still names this process (a successor may already own it).</summary>
    public bool ReleasePidFile()
    {
        try
        {
            if (ReadPid() == _ownPid)
            {
                File.Delete(_pidFile);
                return true;
            }
        }
        catch (IOException)
        {
            // Best effort at exit.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort at exit.
        }

        return false;
    }

    private int? ReadPid()
    {
        try
        {
            return int.TryParse(File.ReadAllText(_pidFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                   && pid > 0
                ? pid
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WritePidAtomically()
    {
        var temp = _pidFile + "." + _ownPid.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, _ownPid.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, _pidFile, overwrite: true);
    }

    private FileStream AcquireLock(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(_pidFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new IOException($"Another hot restart holds {_pidFile}.lock: {ex.Message}", ex);
            }
        }
    }
}

/// <summary>
///     UDP and HTTP/3 binds for a hot-restart successor. The old process owns those UDP ports until it exits, so the
///     binds wait: poll every <c>poll</c> until the previous process is gone, then run each bind. If it never exits
///     within <c>timeout</c> the binds are skipped (the successor keeps serving TCP) and an error is logged. The
///     previous process is never killed.
/// </summary>
internal static class HotRestartDeferredBind
{
    public static async Task<bool> RunAsync(
        int previousPid,
        IHotRestartPlatform platform,
        IReadOnlyList<Action> binds,
        TimeSpan timeout,
        TimeSpan poll,
        Action<string> logError,
        CancellationToken cancellationToken)
    {
        if (binds.Count == 0)
        {
            return true;
        }

        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (platform.IsProcessAlive(previousPid))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    logError(
                        $"Hot restart: process {previousPid} did not exit within {timeout.TotalSeconds:0}s; " +
                        "UDP and HTTP/3 listeners stay off (TCP keeps serving).");
                    return false;
                }

                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        var allBound = true;
        foreach (var bind in binds)
        {
            try
            {
                bind();
            }
            catch (Exception ex)
            {
                allBound = false;
                logError("Hot restart: deferred listener failed to bind: " + ex.Message);
            }
        }

        return allBound;
    }
}

/// <summary>Linux implementation: <c>/proc</c> for liveness and program identity, libc <c>kill</c> for SIGUSR2.</summary>
internal sealed partial class LinuxHotRestartPlatform : IHotRestartPlatform
{
    /// <summary>Linux SIGUSR2. <see cref="PosixSignalRegistration" /> does not accept it (throws "Operation not supported").</summary>
    internal const int SigUsr2 = 12;

    public bool IsProcessAlive(int pid) => Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture));

    public bool IsSameProgram(int pid)
    {
        try
        {
            // comm is the kernel's (truncated) executable name; both sides come from the same source.
            var other = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/comm").Trim();
            var own = File.ReadAllText("/proc/self/comm").Trim();
            return other.Length > 0 && string.Equals(other, own, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool SignalHandover(int pid) => Kill(pid, SigUsr2) == 0;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}

/// <summary>
///     Watches for a hot-restart handover on the predecessor. <see cref="PosixSignalRegistration" /> cannot register
///     SIGUSR2 (it throws), so this polls the pid file: when it names a different live process of the same program the
///     successor has taken over. SIGUSR2 from the successor still wakes the poll early when the libc handler is armed.
/// </summary>
internal sealed partial class HotRestartHandoverWatch : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private HotRestartHandoverWatch(string pidFile, int ownPid, IHotRestartPlatform platform, Action onHandover)
    {
        _loop = Task.Run(() => PollAsync(pidFile, ownPid, platform, onHandover, _cts.Token));
        TryArmSigUsr2Wakeup();
    }

    /// <summary>Starts watching. Dispose to stop.</summary>
    public static HotRestartHandoverWatch Start(
        string pidFile, int ownPid, IHotRestartPlatform platform, Action onHandover) =>
        new(pidFile, ownPid, platform, onHandover);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled.
        }

        _cts.Dispose();
        TryDisarmSigUsr2Wakeup();
    }

    private static async Task PollAsync(
        string pidFile,
        int ownPid,
        IHotRestartPlatform platform,
        Action onHandover,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(pidFile)
                    && int.TryParse((await File.ReadAllTextAsync(pidFile, cancellationToken).ConfigureAwait(false)).Trim(), out var pid)
                    && pid > 0
                    && pid != ownPid
                    && platform.IsProcessAlive(pid)
                    && platform.IsSameProgram(pid))
                {
                    onHandover();
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                if (ex is OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // SIGUSR2 is not in PosixSignalRegistration. A no-op libc handler still lets kill(SIGUSR2) interrupt a sleep on
    // some hosts; the pid-file poll is the source of truth either way.
    private static void TryArmSigUsr2Wakeup()
    {
        try
        {
            _ = Signal(LinuxHotRestartPlatform.SigUsr2, SigIgn);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Non-glibc; the pid-file poll alone is enough.
        }
    }

    private static void TryDisarmSigUsr2Wakeup()
    {
        try
        {
            _ = Signal(LinuxHotRestartPlatform.SigUsr2, SigDfl);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Ignore.
        }
    }

    private static readonly IntPtr SigIgn = new(1); // SIG_IGN
    private static readonly IntPtr SigDfl = IntPtr.Zero; // SIG_DFL

    [LibraryImport("libc", EntryPoint = "signal", SetLastError = true)]
    private static partial IntPtr Signal(int signum, IntPtr handler);
}
