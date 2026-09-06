using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Titanium.Cli.Service;

/// <summary>
/// Interactive elevation for machine-service commands: Windows UAC or Unix sudo.
/// Non-interactive sessions (CI, redirected IO, <c>TITANIUM_NO_ELEVATE=1</c>) skip the
/// prompt so the existing Administrator/sudo error can be printed.
/// </summary>
internal static class PrivilegePrompt
{
    internal const string RelaunchFlag = "--internal-elevated-relaunch";
    internal const string ParentPidFlag = "--internal-parent-pid";
    internal const string NoElevateEnv = "TITANIUM_NO_ELEVATE";

    internal static bool HasRelaunchFlag { get; private set; }
    internal static uint? ParentPid { get; private set; }

    internal static void ResetForTests()
    {
        HasRelaunchFlag = false;
        ParentPid = null;
    }

    internal static bool IsElevated() => Environment.IsPrivilegedProcess;

    /// <summary>Strip hidden relaunch flags so command parsers never see them.</summary>
    internal static string[] TakeInternalArgs(string[] args)
    {
        var list = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == RelaunchFlag)
            {
                HasRelaunchFlag = true;
                continue;
            }

            if (args[i] == ParentPidFlag && i + 1 < args.Length
                && uint.TryParse(args[i + 1], out var pid))
            {
                ParentPid = pid;
                i++;
                continue;
            }

            list.Add(args[i]);
        }

        return list.ToArray();
    }

    /// <summary>
    /// After a Windows UAC relaunch, attach to the original console so output stays
    /// in the user's terminal instead of a new window.
    /// </summary>
    internal static void TryAttachParentConsole()
    {
        if (!OperatingSystem.IsWindows() || ParentPid is not { } pid)
        {
            return;
        }

        try
        {
            FreeConsole();
            if (!AttachConsole(pid))
            {
                return;
            }

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // Keep the elevated console Windows allocated.
        }
    }

    /// <summary>
    /// <see langword="null"/> when this process should continue (already elevated, or
    /// not interactive — caller prints the fallback). An <see cref="int"/> is a final
    /// exit code (relaunched child, cancelled prompt, or failed relaunch).
    /// </summary>
    internal static async Task<int?> EnsureOrRelaunchAsync(string[] commandArgs)
    {
        if (IsElevated())
        {
            return null;
        }

        if (HasRelaunchFlag)
        {
            AsyncConsole.WriteError(FallbackMessage());
            return 1;
        }

        if (!CanPromptInteractively())
        {
            return null;
        }

        var relaunchArgs = AbsolutizeConfigArgs(commandArgs);
        AsyncConsole.WriteLine(InteractivePromptMessage());
        await AsyncConsole.FlushAsync().ConfigureAwait(false);

        return OperatingSystem.IsWindows()
            ? await RelaunchWindowsAsync(relaunchArgs).ConfigureAwait(false)
            : await RelaunchSudoAsync(relaunchArgs).ConfigureAwait(false);
    }

    internal static bool CanPromptInteractively()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(NoElevateEnv), "1", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }
        catch
        {
            return false;
        }
    }

    internal static string InteractivePromptMessage()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Administrator permission is required. Approve the Windows security prompt to continue.";
        }

        return "Root permission is required. Enter your password if asked (sudo).";
    }

    internal static string FallbackMessage()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Administrator privileges required. Re-run from an elevated prompt (Run as Administrator).";
        }

        return "Root privileges required. Re-run with sudo (or use --user).";
    }

    internal static string[] AbsolutizeConfigArgs(string[] args)
    {
        var copy = args.ToArray();
        for (var i = 0; i < copy.Length - 1; i++)
        {
            if (copy[i] is "-c" or "--config")
            {
                try
                {
                    copy[i + 1] = Path.GetFullPath(copy[i + 1]);
                }
                catch
                {
                    // Leave as-is; install validation will report the path error.
                }
            }
        }

        return copy;
    }

    internal static string JoinWindowsArguments(IEnumerable<string> args) =>
        string.Join(' ', args.Select(ServiceUnitFactory.QuoteWindowsArg));

    [SupportedOSPlatform("windows")]
    private static async Task<int> RelaunchWindowsAsync(string[] commandArgs)
    {
        var (fileName, prefix) = ServiceDefaults.ResolveRelaunchTarget();
        var forwarded = new List<string>(prefix);
        forwarded.AddRange(commandArgs);
        forwarded.Add(RelaunchFlag);
        forwarded.Add(ParentPidFlag);
        forwarded.Add(Environment.ProcessId.ToString());

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = JoinWindowsArguments(forwarded),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
            ErrorDialog = false,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                AsyncConsole.WriteError("Permission prompt cancelled.");
                return 1;
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AsyncConsole.WriteError("Permission prompt cancelled.");
            return 1;
        }
        catch (Win32Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            AsyncConsole.WriteError(FallbackMessage());
            return 1;
        }
    }

    private static async Task<int> RelaunchSudoAsync(string[] commandArgs)
    {
        var sudo = ResolveSudoPath();
        if (sudo is null)
        {
            AsyncConsole.WriteError(FallbackMessage());
            return 1;
        }

        var (fileName, prefix) = ServiceDefaults.ResolveRelaunchTarget();
        var psi = new ProcessStartInfo
        {
            FileName = sudo,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(fileName);
        foreach (var a in prefix)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in commandArgs)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add(RelaunchFlag);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                AsyncConsole.WriteError(FallbackMessage());
                return 1;
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            AsyncConsole.WriteError(FallbackMessage());
            return 1;
        }
    }

    internal static string? ResolveSudoPath()
    {
        foreach (var candidate in new[] { "/usr/bin/sudo", "/usr/local/bin/sudo" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
