using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Discovery;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>
///     ServiceDiscovery TryStart / file-mode leftovers that are cheap and Windows-Sonar-visible.
///     Bug classes: unknown modes throw, missing file path starts a watcher anyway, empty destinations wipe clusters.
/// </summary>
[TestClass]
public class ServiceDiscoveryEdgeBehaviorTests
{
    [TestMethod]
    public void TryStart_WithoutMode_ReturnsNull()
    {
        var context = new PlusActivationContext { ProxyServer = new object() };
        Assert.IsNull(ServiceDiscovery.TryStart(context, new Dictionary<string, string>()));
    }

    [TestMethod]
    public void TryStart_UnknownMode_ReturnsInstanceWithoutThrowing()
    {
        // Bug class: unknown discovery.mode aborts Plus activation with an exception.
        var logger = new RecordingLogger();
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Logger = logger,
            ClusterManager = new ClusterManager(),
        };
        using var discovery = ServiceDiscovery.TryStart(context, new Dictionary<string, string>
        {
            ["discovery.mode"] = "zookeeper",
        });
        Assert.IsNotNull(discovery);
        StringAssert.Contains(logger.Text, "unknown mode");
    }

    [TestMethod]
    public void FileMode_MissingPath_DoesNotThrow()
    {
        var logger = new RecordingLogger();
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Logger = logger,
            ClusterManager = new ClusterManager(),
        };
        using var discovery = ServiceDiscovery.TryStart(context, new Dictionary<string, string>
        {
            ["discovery.mode"] = "file",
        });
        Assert.IsNotNull(discovery);
        StringAssert.Contains(logger.Text, "discovery.file");
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task FileMode_AppliesClusters_AndIgnoresWhitespaceOnlyRewrite()
    {
        // Bug class: a truncate/replace that briefly writes whitespace clears applied clusters.
        var path = Path.Combine(Path.GetTempPath(), "twp-discovery-edge-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"clusters":[{"id":"from-file","destinations":[{"id":"a","address":"10.0.0.1","port":8080}]}]}
            """);

        try
        {
            var manager = new ClusterManager();
            var context = new PlusActivationContext
            {
                ProxyServer = new object(),
                ClusterManager = manager,
            };
            using var discovery = ServiceDiscovery.TryStart(context, new Dictionary<string, string>
            {
                ["discovery.mode"] = "file",
                ["discovery.file"] = path,
            });
            Assert.IsNotNull(discovery);

            await WaitUntilAsync(() => manager.Snapshot.Clusters.ContainsKey("from-file"));
            Assert.AreEqual("10.0.0.1", manager.Snapshot.Clusters["from-file"].Destinations[0].Address);

            await File.WriteAllTextAsync(path, "   \n");
            await Task.Delay(800);
            Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("from-file"),
                "whitespace-only rewrite must not clear last applied clusters");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("condition not met");
            }

            await Task.Delay(50);
        }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_text)
            {
                _text.AppendLine(formatter(state, exception));
            }
        }
    }
}
