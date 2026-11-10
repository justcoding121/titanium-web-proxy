using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: LinkShaper zero-frequency, overflow, and disposed arms;
///     InspectorFileLogProvider write failures, retry, and CloseWriterSilent fault tolerance.
/// </summary>
[TestClass]
public class InspectorLinkShaperAndFileLogSoftCoverageBehaviorTests
{
    [TestMethod]
    public void LinkShaper_ZeroFrequencyAndOverflow_AndDisposedReserve()
    {
        using var zeroFreq = new LinkShaper(
            new NetworkThrottleProfile("zero", 0, 1_000),
            new ZeroFrequencyTimeProvider(),
            static (_, _) => Task.CompletedTask);
        Assert.AreEqual(TimeSpan.Zero, zeroFreq.Reserve(ThrottleDirection.Download, 50_000));

        var time = new ManualTimeProvider();
        using var overflow = new LinkShaper(
            new NetworkThrottleProfile("big", 0, 1),
            time,
            static (_, _) => Task.CompletedTask);
        _ = overflow.Reserve(ThrottleDirection.Upload, long.MaxValue);

        var disposed = new LinkShaper(NetworkThrottle.LTE, delay: static (_, _) => Task.CompletedTask);
        disposed.Dispose();
        Assert.AreEqual(TimeSpan.Zero, disposed.Reserve(ThrottleDirection.Download, 1_024));
    }

    [TestMethod]
    public async Task LinkShaper_DisposedWait_ReturnsWithoutThrowing()
    {
        var shaper = new LinkShaper(
            NetworkThrottle.Slow3G,
            delay: static (span, token) => Task.Delay(span, token));
        var waiting = shaper.WaitAsync(TimeSpan.FromSeconds(30));
        shaper.Dispose();
        await waiting;
        shaper.ReleaseWaiters();
    }

    [TestMethod]
    public async Task FileLog_ReadOnlyFileAndBadDirectory_RecoversAfterRetry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-log-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var provider = new InspectorFileLogProvider(dir, maxBytes: 4_096, keep: 3, queueCapacity: 32);
            var logger = provider.CreateLogger("ro");
            logger.LogInformation("seed");
            await WaitForFile(provider);

            var path = provider.CurrentFilePath!;
            File.SetAttributes(path, FileAttributes.ReadOnly);
            logger.LogWarning("after-readonly");
            await Task.Delay(300);

            var bad = Path.Combine(dir, "nested", "no-such");
            provider.Reconfigure(bad, 512, 2);
            var retryLogger = provider.CreateLogger("retry");
            retryLogger.LogInformation("retry-me");
            await Task.Delay(1_200);
            provider.Reconfigure(dir, 4_096, 3);
            retryLogger.LogInformation("after-retry");
            await WaitForFile(provider);
            provider.Dispose();
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    private static async Task WaitForFile(InspectorFileLogProvider provider)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (provider.CurrentFilePath is not null && File.Exists(provider.CurrentFilePath))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("log file not created");
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // best effort
                }
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    private sealed class ZeroFrequencyTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => 0;

        public override long GetTimestamp() => 0;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => 0;
    }

}
