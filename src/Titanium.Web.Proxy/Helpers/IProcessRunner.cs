using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>Result of running an external process.</summary>
internal sealed class ProcessRunResult
{
    public ProcessRunResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? string.Empty;
        StandardError = standardError ?? string.Empty;
    }

    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Abstraction over process launch so unit tests can fake OS tools.</summary>
internal interface IProcessRunner
{
    /// <summary>
    ///     Runs <paramref name="fileName"/> with <paramref name="arguments"/> and captures stdout/stderr.
    ///     Returns null when the executable cannot be started or when <paramref name="timeout"/> elapses.
    /// </summary>
    ProcessRunResult? Run(
        string fileName,
        string arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null);
}

/// <summary>Default <see cref="IProcessRunner"/> using <see cref="System.Diagnostics.Process"/>.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    ///     Default wall-clock bound for trust helpers (<c>security</c>, <c>certutil</c>,
    ///     <c>osascript</c>, package managers) so UI/off-UI awaits cannot wedge forever.
    /// </summary>
    public static readonly TimeSpan DefaultTrustToolTimeout = TimeSpan.FromSeconds(15);

    public ProcessRunResult? Run(
        string fileName,
        string arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? string.Empty
            };

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    if (pair.Value is null)
                        psi.Environment.Remove(pair.Key);
                    else
                        psi.Environment[pair.Key] = pair.Value;
                }
            }

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var limit = timeout ?? DefaultTrustToolTimeout;
            // Read streams asynchronously so a full pipe buffer cannot deadlock WaitForExit.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Math.Clamp(limit.TotalMilliseconds, 1, int.MaxValue)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort kill
                }

                try
                {
                    process.WaitForExit(2000);
                }
                catch
                {
                    // ignore
                }

                return null;
            }

            // Ensure stream reads finish after exit (should be immediate).
            if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(2)))
                return null;

            return new ProcessRunResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            return null;
        }
    }
}
