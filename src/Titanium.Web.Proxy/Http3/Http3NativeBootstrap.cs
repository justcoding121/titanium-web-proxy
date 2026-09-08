using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
/// Ensures app-local MsQuic natives are visible to <see cref="QuicListener"/> on macOS
/// framework-dependent hosts (typical Debug / <c>dotnet run</c>).
/// </summary>
/// <remarks>
/// On non-Windows, <see cref="QuicListener.IsSupported"/> loads MsQuic by leaf name only
/// (<c>libmsquic</c>), not from <see cref="AppContext.BaseDirectory"/>. Self-contained
/// publishes place <c>System.Net.Quic.dll</c> next to the bundled dylibs so AssemblyDirectory
/// search works. Framework-dependent builds keep Quic in the shared framework directory, so
/// copying dylibs beside the app is not enough unless <c>DYLD_FALLBACK_LIBRARY_PATH</c>
/// (or <c>DYLD_LIBRARY_PATH</c>) includes that folder — set before process start.
/// <para>
/// Re-launch uses a child process. The parent must forward POSIX termination/reload signals
/// to that child (and cancel the parent's default terminate) so <c>kill -HUP &lt;started-pid&gt;</c>,
/// SIGTERM from a service manager, and Ctrl+C reach the process that actually runs the proxy.
/// </para>
/// </remarks>
public static class Http3NativeBootstrap
{
    internal const string ReexecMarkerEnv = "TWP_HTTP3_REEXEC";
    internal const string SkipReexecEnv = "TWP_SKIP_HTTP3_REEXEC";

    /// <summary>
    /// When macOS app-local <c>libmsquic.dylib</c> is present but dyld cannot see it yet,
    /// re-launches the current process with <c>DYLD_FALLBACK_LIBRARY_PATH</c> pointing at
    /// <see cref="AppContext.BaseDirectory"/>. No-ops on Windows/Linux, self-contained layouts,
    /// when natives are missing, or when the library path already includes the app directory.
    /// </summary>
    /// <param name="args">Application arguments (as passed to <c>Main</c>), used when relaunching.</param>
    public static void EnsureAppLocalMsQuicVisible(string[]? args = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(SkipReexecEnv), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(ReexecMarkerEnv), "1", StringComparison.Ordinal))
        {
            return;
        }

        var baseDir = NormalizeDir(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(baseDir))
        {
            return;
        }

        var msquic = Path.Combine(baseDir, "libmsquic.dylib");
        if (!File.Exists(msquic))
        {
            return;
        }

        // Self-contained / app-local framework: System.Net.Quic lives next to the dylibs.
        if (IsQuicAssemblyBesideApp(baseDir))
        {
            return;
        }

        if (DyldSearchPathContains(baseDir))
        {
            return;
        }

        RelaunchWithDyldFallback(baseDir, args ?? Array.Empty<string>());
    }

    private static bool IsQuicAssemblyBesideApp(string baseDir)
    {
        try
        {
            var location = typeof(QuicListener).Assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                // Single-file: Quic is embedded; BaseDirectory natives are the intended probe path.
                return true;
            }

            var quicDir = NormalizeDir(Path.GetDirectoryName(location));
            if (string.IsNullOrEmpty(quicDir))
            {
                return false;
            }

            return PathsEqual(quicDir, baseDir)
                   || quicDir.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool DyldSearchPathContains(string baseDir)
    {
        foreach (var key in new[] { "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_LIBRARY_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var part in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(p => PathsEqual(NormalizeDir(p), baseDir)))
            {
                return true;
            }
        }

        return false;
    }

    private static void RelaunchWithDyldFallback(string baseDir, string[] appArgs)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath))
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        AppendRelaunchArguments(psi, processPath, appArgs);

        var existingFallback = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH");
        psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = string.IsNullOrEmpty(existingFallback)
            ? baseDir
            : baseDir + ":" + existingFallback;
        psi.Environment[ReexecMarkerEnv] = "1";

        try
        {
            using var child = Process.Start(psi);
            if (child is null)
            {
                return;
            }

            // Parent keeps the original PID that launchd/shell/probes signal. Forward those
            // signals to the child that actually hosts Quic + the proxy, and cancel the parent's
            // default terminate so SIGHUP (reload) does not exit 129 before the child sees it.
            using var signalForwarders = ForwardUnixSignalsToChild(child);
            child.WaitForExit();
            Environment.Exit(child.ExitCode);
        }
        catch
        {
            // Leave the original process running; Quic may stay unsupported.
        }
    }

    /// <summary>
    /// Registers parent-side POSIX handlers that forward SIGHUP/SIGINT/SIGTERM to
    /// <paramref name="child"/> and cancel the parent's default terminate action.
    /// </summary>
    internal static IDisposable ForwardUnixSignalsToChild(Process child)
    {
        if (OperatingSystem.IsWindows())
        {
            return EmptyDisposable.Instance;
        }

        var registrations = new List<PosixSignalRegistration>(3);
        void Forward(PosixSignal signal, int signo)
        {
            registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
            {
                ctx.Cancel = true;
                try
                {
                    if (!child.HasExited)
                    {
                        _ = NativeKill(child.Id, signo);
                    }
                }
                catch
                {
                    // Child may have exited between HasExited and kill.
                }
            }));
        }

        // SIGHUP=1, SIGINT=2, SIGTERM=15 on Linux and macOS.
        Forward(PosixSignal.SIGHUP, 1);
        Forward(PosixSignal.SIGINT, 2);
        Forward(PosixSignal.SIGTERM, 15);
        return new SignalForwarderLease(registrations);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);

    private static void AppendRelaunchArguments(ProcessStartInfo psi, string processPath, string[] appArgs)
    {
        var hostName = Path.GetFileNameWithoutExtension(processPath);
        var isDotnetHost = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost)
        {
            // `dotnet path/to/app.dll …args` — keep the entry assembly path then app args.
            var entry = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entry) && File.Exists(entry))
            {
                psi.ArgumentList.Add(entry);
                foreach (var a in appArgs)
                {
                    psi.ArgumentList.Add(a);
                }

                return;
            }
        }

        // AppHost: argv is just the application arguments.
        foreach (var a in appArgs)
        {
            psi.ArgumentList.Add(a);
        }
    }

    private static string NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private sealed class SignalForwarderLease : IDisposable
    {
        private readonly List<PosixSignalRegistration> _registrations;

        public SignalForwarderLease(List<PosixSignalRegistration> registrations) =>
            _registrations = registrations;

        public void Dispose()
        {
            foreach (var reg in _registrations)
            {
                reg.Dispose();
            }

            _registrations.Clear();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
