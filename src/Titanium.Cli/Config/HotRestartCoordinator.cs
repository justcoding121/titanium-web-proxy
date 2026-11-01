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
///     Pid-file side of <c>server.hotRestart</c> (Linux, TCP only). The new process binds with SO_REUSEPORT and becomes
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

/// <summary>Linux implementation: <c>/proc</c> for liveness and program identity, libc <c>kill</c> for SIGUSR2.</summary>
internal sealed partial class LinuxHotRestartPlatform : IHotRestartPlatform
{
    /// <summary>The raw Linux number of SIGUSR2, accepted by <see cref="PosixSignalRegistration" /> as a cast.</summary>
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
