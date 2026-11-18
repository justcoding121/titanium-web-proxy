using System;
using System.IO;

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
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || !IsAllDigits(name))
                continue;

            string cmdline;
            try
            {
                cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(cmdline))
                continue;

            var parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var joined = string.Join(' ', parts);
            if (joined.Contains("--clear-system-proxy", StringComparison.Ordinal))
                continue;

            var exe = parts[0];
            if (exe.EndsWith("/TitaniumInspector", StringComparison.Ordinal) ||
                string.Equals(exe, "TitaniumInspector", StringComparison.Ordinal) ||
                string.Equals(Path.GetFileName(exe), "TitaniumInspector", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsAllDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return value.Length > 0;
    }
}
