using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorFileLogProviderResilienceTests
{
    /// <summary>
    ///     Reconfigure() closes the stream writer from the UI thread while the writer loop is mid-write.
    ///     That race must never kill the loop: a dead loop silently stops all file logging for the run.
    /// </summary>
    [TestMethod]
    public async Task WriterLoop_SurvivesConcurrentReconfigure_AndKeepsLogging()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "twp-logres-a-" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "twp-logres-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var provider = new InspectorFileLogProvider(dirA, 1024 * 1024, 5);
        try
        {
            var logger = provider.CreateLogger("stress");
            using var cts = new CancellationTokenSource();
            var producer = Task.Run(() =>
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    var index = i++;
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("line {Index}", index);
                    }

                    if (i % 64 == 0)
                    {
                        Thread.Yield();
                    }
                }
            });

            for (var i = 0; i < 300; i++)
            {
                provider.Reconfigure(i % 2 == 0 ? dirB : dirA, 1024 * 1024, 5);
                await Task.Delay(2);
            }

            cts.Cancel();
            await producer;

            var writerTask = (Task)typeof(InspectorFileLogProvider)
                .GetField("_writerTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(provider)!;
            Assert.IsFalse(writerTask.IsCompleted, "writer loop must still be running after concurrent Reconfigure: " + writerTask.Status + " " + writerTask.Exception);

            provider.Reconfigure(dirA, 1024 * 1024, 5);
            var before = CountLines(dirA);
            logger.LogWarning("probe-after-stress");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (CountLines(dirA) <= before && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.IsTrue(CountLines(dirA) > before, "a line logged after the stress must reach the file");
        }
        finally
        {
            provider.Dispose();
            foreach (var dir in new[] { dirA, dirB })
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // best effort temp cleanup
                }
            }
        }
    }

    private static int CountLines(string dir) =>
        Directory.GetFiles(dir, "titanium-inspector-*.log").Sum(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var count = 0;
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
        });
}

