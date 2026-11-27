using System;
using System.IO;

namespace Titanium.Tls;

/// <summary>Reads the operating system's FIPS mode without native calls.</summary>
public static class FipsEnvironment
{
    private static IFipsProbe _probe = SystemFipsProbe.Instance;

    /// <summary>
    /// True when Windows reports <c>FipsAlgorithmPolicy\Enabled</c> as 1, or Linux <c>/proc/sys/crypto/fips_enabled</c> is 1.
    /// macOS is false.
    /// </summary>
    /// <returns><see langword="true"/> when FIPS mode is on.</returns>
    public static bool IsEnabled() => Evaluate(_probe);

    internal static bool SystemIsEnabled() => Evaluate(SystemFipsProbe.Instance);

    internal static bool Evaluate(IFipsProbe probe)
    {
        if (probe.IsMacOs)
        {
            return false;
        }

        if (probe.IsWindows)
        {
            return probe.WindowsEnabled == 1;
        }

        if (!probe.IsLinux)
        {
            return false;
        }

        string? text = probe.LinuxFile;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        return trimmed.Length == 1 && trimmed[0] == '1';
    }

    internal static bool OverrideIsSet()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE"),
            "1",
            StringComparison.Ordinal);
    }

    internal static void Use(IFipsProbe probe) => _probe = probe;

    internal static void Reset() => _probe = SystemFipsProbe.Instance;
}

internal interface IFipsProbe
{
    bool IsWindows { get; }

    bool IsLinux { get; }

    bool IsMacOs { get; }

    int? WindowsEnabled { get; }

    string? LinuxFile { get; }
}

internal sealed class SystemFipsProbe : IFipsProbe
{
    internal static SystemFipsProbe Instance { get; } = new();

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool IsMacOs => OperatingSystem.IsMacOS();

    public int? WindowsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                object? value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\FipsAlgorithmPolicy",
                    "Enabled",
                    null);
                return value is int number ? number : null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public string? LinuxFile
    {
        get
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                const string path = "/proc/sys/crypto/fips_enabled";
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
