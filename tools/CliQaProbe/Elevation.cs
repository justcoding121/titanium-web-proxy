using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Titanium.Cli.QaProbe;

public static class Elevation
{
    public const string QaServiceName = "titanium-qa-probe";

    public static bool IsElevated()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        try
        {
            return geteuid() == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
