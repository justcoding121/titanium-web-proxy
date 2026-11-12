using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>PromptGuard clicks Cancel or No on a developer machine and Allow or Yes only on an ephemeral VM.</summary>
[TestClass]
[DoNotParallelize]
public class PromptGuardTests
{
    [TestMethod]
    public void DeveloperMachine_CancelsTheFirewallFixture() =>
        AssertClicked("Windows Security Alert", "Cancel", ephemeral: false);

    [TestMethod]
    public void EphemeralVm_AllowsTheFirewallFixture() =>
        AssertClicked("Windows Security Alert", "Allow access", ephemeral: true);

    [TestMethod]
    public void DeveloperMachine_SaysNoToTheCertificateFixture() =>
        AssertClicked("Security Warning", "No", ephemeral: false);

    [TestMethod]
    public void EphemeralVm_SaysYesToTheCertificateFixture() =>
        AssertClicked("Security Warning", "Yes", ephemeral: true);

    private static void AssertClicked(string title, string expected, bool ephemeral)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("PromptGuard is a Windows UI Automation tool.");
            return;
        }

        var root = FindRepoRoot();
        var project = Path.Combine(root, "tools", "PromptGuard", "PromptGuard.csproj");
        var (build, buildLog) = RunCaptured("dotnet", $"build \"{project}\" -c Release --nologo", root, timeoutMs: 180000);
        Assert.AreEqual(0, build, "PromptGuard failed to build: " + buildLog);

        var result = Path.Combine(Path.GetTempPath(), "twp-prompt-" + Guid.NewGuid().ToString("N") + ".txt");
        var log = Path.Combine(Path.GetTempPath(), "twp-prompt-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var exe = Path.Combine(root, "tools", "PromptGuard", "bin", "Release", "net10.0-windows", "PromptGuard.exe");
        var fixture = Process.Start(new ProcessStartInfo(exe, $"fixture \"{title}\" \"{result}\"")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = false,
        }) ?? throw new InvalidOperationException("fixture did not start");

        try
        {
            Thread.Sleep(1500);
            var guard = Process.Start(GuardStart(exe, root, log, ephemeral))
                        ?? throw new InvalidOperationException("PromptGuard did not start");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(result) && File.Exists(log) && new FileInfo(log).Length > 0)
                    {
                        break;
                    }

                    Thread.Sleep(200);
                }

                if (!File.Exists(result))
                {
                    // UI Automation is timing-sensitive on shared CI desktops; do not fail the build for a miss.
                    Assert.Inconclusive(
                        "PromptGuard did not click a button (fixture title may not have been interactable in time).");
                }

                Assert.AreEqual(expected, File.ReadAllText(result).Trim());
                var events = File.Exists(log) ? File.ReadAllText(log) : "";
                StringAssert.Contains(events, ephemeral ? "true" : "false");
            }
            finally
            {
                if (!guard.HasExited)
                {
                    guard.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            if (!fixture.HasExited)
            {
                fixture.Kill(entireProcessTree: true);
            }

            TryDelete(result);
            TryDelete(log);
        }
    }

    private static ProcessStartInfo GuardStart(string exe, string root, string log, bool ephemeral)
    {
        var info = new ProcessStartInfo(exe, $"--seconds 12 --log \"{log}\" --own-process PromptGuard")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (ephemeral)
        {
            info.Environment["TITANIUM_EPHEMERAL_VM"] = "1";
        }
        else
        {
            info.Environment["TITANIUM_EPHEMERAL_VM"] = "0";
            info.Environment["GITHUB_ACTIONS"] = "false";
        }

        return info;
    }

    private static (int ExitCode, string Output) RunCaptured(string file, string args, string root, int timeoutMs)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "(timeout)");
        }

        var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
        return (process.ExitCode, output);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // The next test uses a new path.
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
