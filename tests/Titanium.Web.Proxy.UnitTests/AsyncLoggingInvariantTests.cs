using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
/// Guards the product logging invariant: diagnostic helpers must not sync-write to Console.
/// </summary>
[TestClass]
public partial class AsyncLoggingInvariantTests
{
    [GeneratedRegex(@"Console\.(Write|WriteLine|Error\.Write)")]
    private static partial Regex PlusLogConsoleRegex();

    [GeneratedRegex(@"Console\.(Out|Error)\.Write(Line)?\(")]
    private static partial Regex AsyncConsoleOutErrorRegex();

    [GeneratedRegex(@"\bConsole\.Write(Line)?\(")]
    private static partial Regex AsyncConsoleWriteRegex();

    [GeneratedRegex(@"Console\.")]
    private static partial Regex ConsoleRegex();

    [TestMethod]
    public void PlusLog_Source_Has_No_Sync_Console_Writes()
    {
        var path = FindRepoFile(Path.Combine("src", "Titanium.Plus", "PlusLog.cs"));
        var text = File.ReadAllText(path);
        StringAssert.DoesNotMatch(text, PlusLogConsoleRegex());
    }

    [TestMethod]
    public void AsyncConsole_Enqueue_Does_Not_Call_Console_Synchronously()
    {
        var path = FindRepoFile(Path.Combine("src", "Titanium.Cli", "AsyncConsole.cs"));
        var text = File.ReadAllText(path);
        Assert.IsTrue(text.Contains("TryWrite", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("WriteLineAsync", StringComparison.Ordinal));
        StringAssert.DoesNotMatch(text, AsyncConsoleOutErrorRegex());
        StringAssert.DoesNotMatch(text, AsyncConsoleWriteRegex());
    }

    [TestMethod]
    public void ChannelLoggerProviderBase_OnSinkDisposalLeaked_Is_Silent()
    {
        var path = FindRepoFile(Path.Combine("src", "Titanium.Web.Proxy", "Logging", "ChannelLoggerProviderBase.cs"));
        var text = File.ReadAllText(path);
        var idx = text.IndexOf("protected virtual void OnSinkDisposalLeaked()", StringComparison.Ordinal);
        Assert.IsTrue(idx >= 0);
        var body = text.Substring(idx, Math.Min(400, text.Length - idx));
        StringAssert.DoesNotMatch(body, ConsoleRegex());
    }

    private static string FindRepoFile(string relativeUnderRepo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeUnderRepo);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        Assert.Fail($"Could not locate {relativeUnderRepo} from {AppContext.BaseDirectory}");
        return null!;
    }
}
