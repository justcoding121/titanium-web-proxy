using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Runs a command with an OS admin prompt (UAC / macOS auth / polkit). Cancel returns null.
/// </summary>
internal interface IElevationPrompt
{
    /// <summary>
    ///     Prompts for elevation and runs <paramref name="fileName"/> <paramref name="arguments"/>.
    ///     Returns the process result, or null if the user cancelled / no GUI / launch failed.
    /// </summary>
    ProcessRunResult? RunElevated(string fileName, string arguments);
}

/// <summary>OS-native elevation: Windows runas, macOS osascript admin, Linux pkexec.</summary>
internal sealed class OsElevationPrompt : IElevationPrompt
{
    private readonly IProcessRunner _runner;

    public OsElevationPrompt(IProcessRunner? runner = null)
    {
        _runner = runner ?? new ProcessRunner();
    }

    public ProcessRunResult? RunElevated(string fileName, string arguments)
    {
        if (RunTime.IsWindows)
            return RunWindowsElevated(fileName, arguments);

        if (RunTime.IsMac)
            return RunMacElevated(fileName, arguments);

        if (RunTime.IsLinux)
            return RunLinuxElevated(fileName, arguments);

        return null;
    }

    private static ProcessRunResult? RunWindowsElevated(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            process.WaitForExit();
            // stdout/stderr not available with UseShellExecute + runas
            return new ProcessRunResult(process.ExitCode, string.Empty, string.Empty);
        }
        catch (Exception)
        {
            // User cancelled UAC or elevation unavailable
            return null;
        }
    }

    private ProcessRunResult? RunMacElevated(string fileName, string arguments)
    {
        // osascript prompts with the standard macOS admin password dialog.
        var shell = EscapeForAppleScript($"'{EscapeSingleQuotes(fileName)}' {arguments}");
        var script = $"do shell script \"{shell}\" with administrator privileges";
        return _runner.Run("/usr/bin/osascript", $"-e {QuoteForProcess(script)}");
    }

    private ProcessRunResult? RunLinuxElevated(string fileName, string arguments)
    {
        // pkexec shows a polkit dialog when a display session is available.
        return _runner.Run("pkexec", $"{QuoteForProcess(fileName)} {arguments}");
    }

    private static string EscapeSingleQuotes(string value) => value.Replace("'", "'\\''");

    private static string EscapeForAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string QuoteForProcess(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.IndexOfAny([' ', '\t', '"', '\'']) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
