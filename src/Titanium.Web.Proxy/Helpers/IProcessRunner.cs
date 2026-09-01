using System.Collections.Generic;

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
    ///     Returns null when the executable cannot be started.
    /// </summary>
    ProcessRunResult? Run(string fileName, string arguments, IDictionary<string, string?>? environment = null,
        string? workingDirectory = null);
}

/// <summary>Default <see cref="IProcessRunner"/> using <see cref="System.Diagnostics.Process"/>.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public ProcessRunResult? Run(string fileName, string arguments, IDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
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

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessRunResult(process.ExitCode, stdout, stderr);
        }
        catch
        {
            return null;
        }
    }
}
