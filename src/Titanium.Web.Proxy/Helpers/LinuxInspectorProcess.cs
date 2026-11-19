using System;
using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Detects a live Titanium Inspector process on Linux (login clear must no-op).
/// </summary>
public static class LinuxInspectorProcess
{
    /// <summary>
    ///     True when <c>/proc</c> lists a TitaniumInspector executable that is not a
    ///     <c>--clear-system-proxy</c> invocation.
    /// </summary>
    /// <param name="procRoot">Override for tests (fake /proc tree).</param>
    public static bool IsRunning(string? procRoot = null)
    {
        if (!OperatingSystem.IsLinux() && procRoot is null)
            return false;

        var root = procRoot ?? "/proc";
        if (!Directory.Exists(root))
            return false;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (IsLiveInspectorProcess(dir))
                return true;
        }

        return false;
    }

    private static bool IsLiveInspectorProcess(string dir)
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name) || !IsAllDigits(name))
            return false;

        if (!TryReadCmdline(dir, out var cmdline))
            return false;

        var parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        if (string.Join(' ', parts).Contains("--clear-system-proxy", StringComparison.Ordinal))
            return false;

        return IsInspectorExecutable(parts[0]);
    }

    private static bool TryReadCmdline(string dir, out string cmdline)
    {
        try
        {
            cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
            return cmdline.Length > 0;
        }
        catch
        {
            cmdline = string.Empty;
            return false;
        }
    }

    private static bool IsInspectorExecutable(string exe) =>
        exe.EndsWith("/TitaniumInspector", StringComparison.Ordinal) ||
        string.Equals(exe, "TitaniumInspector", StringComparison.Ordinal) ||
        string.Equals(Path.GetFileName(exe), "TitaniumInspector", StringComparison.Ordinal);

    private static bool IsAllDigits(string value) =>
        value.Length > 0 && !value.Any(static c => c is < '0' or > '9');
}
