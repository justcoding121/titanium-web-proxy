using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Discover DISPLAY / DBUS from the active graphical login (XFCE, GNOME, KDE, xrdp, etc.).
///     Inspector may be started from Cursor/IDE with a poisoned or missing session bus; gsettings
///     must still target the desktop the user is actually using.
/// </summary>
internal static class LinuxGraphicalSession
{
    private static readonly string[] SessionProcessNames =
    [
        "xfce4-session",
        "gnome-session",
        "gnome-session-binary",
        "gnome-session-b",
        "plasmashell",
        "startplasma-x11",
        "startplasma-wayland",
        "cinnamon-session",
        "mate-session",
        "lxqt-session",
        "budgie-desktop",
        "xrdp-chansrv",
        "x-session-manager",
    ];

    internal static string? TryGetDbusSessionAddress() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
            TryReadFromSessionProcess("DBUS_SESSION_BUS_ADDRESS"));

    internal static string? TryGetDisplay() =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("DISPLAY"),
            TryReadFromSessionProcess("DISPLAY"));

    internal static string? TryReadFromSessionProcess(string variableName)
    {
        foreach (var pid in EnumerateCandidateSessionPids())
        {
            var value = TryReadEnvironVariable(pid, variableName);
            if (!string.IsNullOrWhiteSpace(value) &&
                (variableName != "DBUS_SESSION_BUS_ADDRESS" || IsUsableDbusAddress(value)))
            {
                return value;
            }
        }

        return null;
    }

    internal static bool IsUsableDbusAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        !address.StartsWith("disabled", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<int> EnumerateCandidateSessionPids()
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories("/proc");
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid <= 1)
                continue;

            string? comm = null;
            try
            {
                comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
            }
            catch
            {
                continue;
            }

            if (SessionProcessNames.Any(name =>
                    comm.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    comm.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return pid;
            }
        }
    }

    internal static string? TryReadEnvironVariable(int pid, string variableName)
    {
        try
        {
            var raw = File.ReadAllBytes($"/proc/{pid}/environ");
            var start = 0;
            for (var i = 0; i <= raw.Length; i++)
            {
                if (i != raw.Length && raw[i] != 0)
                    continue;

                var len = i - start;
                if (len > 0)
                {
                    var entry = System.Text.Encoding.UTF8.GetString(raw, start, len);
                    var eq = entry.IndexOf('=');
                    if (eq > 0 &&
                        entry.AsSpan(0, eq).SequenceEqual(variableName.AsSpan()))
                    {
                        return entry[(eq + 1)..];
                    }
                }

                start = i + 1;
            }
        }
        catch
        {
            // not readable or process exited
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
