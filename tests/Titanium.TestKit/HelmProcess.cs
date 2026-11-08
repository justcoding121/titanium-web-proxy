using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.TestKit;

/// <summary>Runs Helm with both pipes drained. A stuck process is killed.</summary>
public static class HelmProcess
{
    public static (int ExitCode, string Output) Run(string file, string arguments)
    {
        // Drain both pipes while Helm runs. Reading one pipe synchronously deadlocks when the other fills.
        // A first Windows launch can outlast a one-minute wait, so the budget is three minutes.
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.WaitForExit(5_000);
            Assert.Fail("helm timed out: " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
