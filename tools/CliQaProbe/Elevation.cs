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

    /// <summary>
    /// Login user for Linux <c>systemd --user</c> / LaunchAgent checks.
    /// Prefer <c>SUDO_USER</c> when the probe was started with sudo; otherwise the current user
    /// when not root. Root without <c>SUDO_USER</c> returns null (no user bus to target).
    /// </summary>
    public static string? TryResolveLoginUser()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.UserName;

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser) &&
            !sudoUser.Equals("root", StringComparison.Ordinal))
            return sudoUser;

        if (!IsElevated())
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) || name.Equals("root", StringComparison.Ordinal)
                ? null
                : name;
        }

        return null;
    }

    public static uint? TryResolveLoginUid(string? user = null)
    {
        user ??= TryResolveLoginUser();
        if (string.IsNullOrEmpty(user))
            return null;

        var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
        if (!string.IsNullOrEmpty(sudoUid) &&
            string.Equals(Environment.GetEnvironmentVariable("SUDO_USER"), user, StringComparison.Ordinal) &&
            uint.TryParse(sudoUid, out var parsed))
            return parsed;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "id",
                Arguments = "-u " + user,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return null;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return uint.TryParse(text, out var uid) ? uid : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryResolveLoginHome(string user)
    {
        var sudoHome = Environment.GetEnvironmentVariable("SUDO_HOME");
        if (!string.IsNullOrEmpty(sudoHome) &&
            string.Equals(Environment.GetEnvironmentVariable("SUDO_USER"), user, StringComparison.Ordinal) &&
            Directory.Exists(sudoHome))
            return sudoHome;

        foreach (var candidate in new[]
                 {
                     Path.Combine("/home", user),
                     Path.Combine("/Users", user),
                 })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Env vars so <c>systemctl --user</c> talks to the login user's bus.</summary>
    public static Dictionary<string, string?>? TryBuildLoginUserEnv()
    {
        var user = TryResolveLoginUser();
        if (user is null)
            return null;

        var uid = TryResolveLoginUid(user);
        var home = TryResolveLoginHome(user);
        if (uid is null || home is null)
            return null;

        var runtime = $"/run/user/{uid.Value}";
        var bus = $"unix:path={runtime}/bus";
        return new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["USER"] = user,
            ["LOGNAME"] = user,
            ["XDG_RUNTIME_DIR"] = runtime,
            ["DBUS_SESSION_BUS_ADDRESS"] = bus,
            // Child is already the login user (or sudo -u); don't try nested elevation prompts.
            ["TITANIUM_NO_ELEVATE"] = "1",
        };
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
