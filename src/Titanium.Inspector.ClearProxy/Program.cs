using System;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Inspector.ClearProxy;

/// <summary>
///     Login / timer / postinst entrypoint. No Avalonia. Exits immediately while Inspector
///     is running so a systemd user timer cannot clear an active capture session.
/// </summary>
internal static class Program
{
    public static int Main()
    {
        if (OperatingSystem.IsLinux() && LinuxInspectorProcess.IsRunning())
            return 0;

        InstalledProxyCleanup.ForceClear();
        return 0;
    }
}
