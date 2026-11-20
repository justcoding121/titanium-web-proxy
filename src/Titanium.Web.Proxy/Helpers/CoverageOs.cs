using System;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     OS and home checks the Windows coverage host can opt into.
///     Production leaves the flags unset, so the properties match the real machine.
/// </summary>
internal static class CoverageOs
{
    internal static bool TreatAsLinuxForTests { get; set; }

    internal static bool TreatAsMacForTests { get; set; }

    /// <summary>When set, file-based helpers read and write this directory instead of the user profile.</summary>
    internal static string? HomeForTests { get; set; }

    internal static bool IsLinux => TreatAsLinuxForTests || OperatingSystem.IsLinux();

    internal static bool IsMac => TreatAsMacForTests || OperatingSystem.IsMacOS();

    internal static string Home =>
        string.IsNullOrWhiteSpace(HomeForTests)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : HomeForTests;
}
