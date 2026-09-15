using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Titanium.Cli.Config;

/// <summary>
/// Cross-platform reload coordination for a running <c>titanium run -c …</c> process:
/// Unix uses SIGHUP (via pid file); Windows uses a named <see cref="EventWaitHandle"/>.
/// </summary>
internal static partial class ConfigReloadGate
{
    private const string PidDirName = "TitaniumWebProxy";

    public static string ConfigKey(string configPath)
    {
        var full = Path.GetFullPath(configPath);
        // Normalize for Windows case-insensitivity so reload finds the same gate.
        if (OperatingSystem.IsWindows())
            full = full.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public static string PidFilePath(string configPath) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PidDirName,
            "run-" + ConfigKey(configPath) + ".pid");

    /// <summary>Windows-only local named event for <c>titanium reload</c>.</summary>
    public static string WindowsEventName(string configPath) =>
        @"Local\TitaniumWebProxy.Reload." + ConfigKey(configPath);

    public static void WritePidFile(string configPath, int pid)
    {
        var path = PidFilePath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void TryDeletePidFile(string configPath)
    {
        try
        {
            var path = PidFilePath(configPath);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public static int? TryReadPid(string configPath)
    {
        try
        {
            var path = PidFilePath(configPath);
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Creates (or opens) the Windows reload event for this config. Caller owns disposal.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static EventWaitHandle CreateWindowsReloadEvent(string configPath, out bool createdNew)
    {
        var name = WindowsEventName(configPath);
        return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew);
    }

    /// <summary>Signals a running Windows <c>run</c> process to reload. Returns false if no waiter.</summary>
    public static bool TrySignalWindowsReload(string configPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            if (!EventWaitHandle.TryOpenExisting(WindowsEventName(configPath), out var handle))
                return false;
            using (handle)
            {
                handle.Set();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sends SIGHUP to <paramref name="pid"/> (Unix). Returns false on failure.</summary>
    public static bool TrySendSighup(int pid)
    {
        if (OperatingSystem.IsWindows())
            return false;
        try
        {
            return NativeKill(pid, 1) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int NativeKill(int pid, int sig);
}
