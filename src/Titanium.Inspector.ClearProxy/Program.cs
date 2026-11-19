using System;
using System.Linq;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Inspector.ClearProxy;

/// <summary>
///     Login / timer / postinst entrypoint. No Avalonia. Exits immediately while Inspector
///     is running so a systemd user timer cannot clear an active capture session.
///     Without <c>--force</c>, Linux also requires on-disk Inspector ownership evidence so a
///     developer's own loopback forwarder is not wiped every five minutes after the deb install.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsLinux() && LinuxInspectorProcess.IsRunning())
            return 0;

        var force = args.Any(static a => string.Equals(a, "--force", StringComparison.Ordinal));
        if (force)
            InstalledProxyCleanup.ForceClear();
        else
            InstalledProxyCleanup.TryForceClearWhenOwned();

        return 0;
    }
}
