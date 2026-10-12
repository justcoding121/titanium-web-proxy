using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorFileLogProviderTests
{
    [TestMethod]
    public async Task CreatesDatedFile_OnFirstLine_AndRollsWithoutOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-filesink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var provider = new InspectorFileLogProvider(dir, maxBytes: 80, keep: 5, queueCapacity: 64);
            Assert.IsNull(provider.CurrentFilePath);

            var logger = provider.CreateLogger("Cat");
            logger.Log(LogLevel.Information, 0, "first-line", null, static (s, _) => s);
            await WaitUntil(() => provider.CurrentFilePath is not null);
            var first = provider.CurrentFilePath!;
            Assert.IsTrue(File.Exists(first));
            Assert.IsTrue(Path.GetFileName(first).StartsWith("titanium-inspector-", StringComparison.Ordinal));

            for (var i = 0; i < 40; i++)
            {
                logger.Log(LogLevel.Information, 0, "pad-" + i + "-xxxxxxxxxxxxxxxx", null, static (s, _) => s);
            }

            await WaitUntil(() =>
            {
                var files = Directory.GetFiles(dir, "titanium-inspector-*.log");
                return files.Length >= 2;
            });

            var firstText = await File.ReadAllTextAsync(first);
            StringAssert.Contains(firstText, "first-line");
            Assert.AreNotEqual(first, provider.CurrentFilePath);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // file handle may still be releasing on Windows
            }
        }
    }

    [TestMethod]
    public async Task Prune_LeavesAtMostKeepFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-filesink-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var provider = new InspectorFileLogProvider(dir, maxBytes: 60, keep: 2, queueCapacity: 64);
            var logger = provider.CreateLogger("Cat");
            for (var i = 0; i < 80; i++)
            {
                logger.Log(LogLevel.Warning, 0, "line-" + i + "-yyyyyyyyyyyyyyyy", null, static (s, _) => s);
            }

            await WaitUntil(() => Directory.GetFiles(dir, "titanium-inspector-*.log").Length >= 2);
            await Task.Delay(200);
            var files = Directory.GetFiles(dir, "titanium-inspector*.log*");
            Assert.IsTrue(files.Length <= 2, "expected at most 2 files, got " + files.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public void FormatLine_MatchesSharedFormatter()
    {
        var entry = new RunLogEntry(
            new DateTime(2026, 10, 12, 14, 30, 5, 123),
            LogLevel.Information,
            "Cat",
            "hello");
        Assert.AreEqual(
            "2026-10-12 14:30:05.123 [INFO ] Cat: hello",
            RunLogFormat.FormatLine(entry));
    }

    [TestMethod]
    public async Task DroppedLines_WriteMarker_WhenQueueTiny()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-filesink-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string? path;
            using (var provider = new InspectorFileLogProvider(dir, maxBytes: 10_000_000, keep: 5, queueCapacity: 1))
            {
                var logger = provider.CreateLogger("Cat");
                for (var i = 0; i < 200; i++)
                {
                    logger.Log(LogLevel.Error, 0, "burst-" + i, null, static (s, _) => s);
                }

                await WaitUntil(() => provider.CurrentFilePath is not null && File.Exists(provider.CurrentFilePath));
                await Task.Delay(300);
                path = provider.CurrentFilePath;
            }

            Assert.IsNotNull(path);
            var text = await File.ReadAllTextAsync(path!);
            StringAssert.Contains(text, "dropped");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("condition not met in time");
    }
}
