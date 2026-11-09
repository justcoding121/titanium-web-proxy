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

    [TestMethod]
    public void TryStart_DnsWithoutName_LogsAndReturnsInstance()
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
            ["discovery.mode"] = "dns",
        });
        Assert.IsNotNull(discovery);
        StringAssert.Contains(logger.Text, "discovery.dnsName");
    }

    [TestMethod]
    public void TryStart_ConsulWithoutUrl_OrNonHttpUrl_LogsAndReturnsInstance()
    {
        var logger = new RecordingLogger();
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Logger = logger,
            ClusterManager = new ClusterManager(),
        };

        using (var missing = ServiceDiscovery.TryStart(context, new Dictionary<string, string>
        {
            ["discovery.mode"] = "consul",
        }))
        {
            Assert.IsNotNull(missing);
            StringAssert.Contains(logger.Text, "discovery.consulUrl");
        }

        using var badScheme = ServiceDiscovery.TryStart(context, new Dictionary<string, string>
        {
            ["discovery.mode"] = "consul",
            ["discovery.consulUrl"] = "ftp://consul.example.test/v1/health/service/web",
        });
        Assert.IsNotNull(badScheme);
        StringAssert.Contains(logger.Text, "absolute http(s) URL");
    }

    [TestMethod]
    public void ParseClustersDocument_AcceptsArrayAndClustersObject_RejectsOtherShapes()
    {
        var array = ServiceDiscovery.ParseClustersDocument(
            """[{"id":"c1","destinations":[{"id":"d1","address":"10.0.0.1","port":80}]}]""");
        Assert.IsNotNull(array);
        Assert.AreEqual(1, array.Count);
        Assert.AreEqual("c1", array[0].Id);

        var wrapped = ServiceDiscovery.ParseClustersDocument(
            """{"Clusters":[{"id":"c2","destinations":[{"id":"d2","address":"10.0.0.2","port":81}]}]}""");
        Assert.IsNotNull(wrapped);
        Assert.AreEqual("c2", wrapped[0].Id);

        Assert.IsNull(ServiceDiscovery.ParseClustersDocument("""{"destinations":[]}"""));
        Assert.IsNull(ServiceDiscovery.ParseClustersDocument("\"just-a-string\""));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ApplyFileAsync_WhitespaceAndInvalidJson_ReturnFalseWithoutApplying()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-discovery-apply-" + Guid.NewGuid().ToString("N") + ".json");
        var manager = new ClusterManager();
        var refreshed = 0;
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            ClusterManager = manager,
            RefreshReverseProxy = () => Interlocked.Increment(ref refreshed),
        };

        try
        {
            await File.WriteAllTextAsync(path, "   \n\t");
            Assert.IsFalse(await ServiceDiscovery.ApplyFileAsync(context, path, CancellationToken.None));

            await File.WriteAllTextAsync(path, "{not-json");
            Assert.IsFalse(await ServiceDiscovery.ApplyFileAsync(context, path, CancellationToken.None));

            await File.WriteAllTextAsync(path, """{"noClusters":true}""");
            Assert.IsFalse(await ServiceDiscovery.ApplyFileAsync(context, path, CancellationToken.None));

            Assert.AreEqual(0, manager.Snapshot.Clusters.Count);
            Assert.AreEqual(0, Volatile.Read(ref refreshed));
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

    [TestMethod]
    public void BuildConsulUrl_AddsPassingIndexAndWait_AndWithQueryDropsStaleKeys()
    {
        var settings = new ConsulSettings(
            new Uri("http://127.0.0.1:8500/v1/health/service/web?passing=stale&keep=1&index=9&wait=1s"),
            "web",
            IntervalMs: 15_000,
            WaitSeconds: 30,
            PassingOnly: true,
            Token: null,
            new BackoffSettings(1000, 30_000));

        var first = ServiceDiscovery.BuildConsulUrl(settings, index: 0);
        StringAssert.Contains(first.Query, "passing=true");
        StringAssert.Contains(first.Query, "keep=1");
        Assert.IsFalse(first.Query.Contains("index=", StringComparison.Ordinal));
        Assert.IsFalse(first.Query.Contains("wait=", StringComparison.Ordinal));

        var blocked = ServiceDiscovery.BuildConsulUrl(settings, index: 42);
        StringAssert.Contains(blocked.Query, "index=42");
        StringAssert.Contains(blocked.Query, "wait=30s");
        StringAssert.Contains(blocked.Query, "passing=true");

        var stripped = ServiceDiscovery.WithQuery(
            new Uri("http://127.0.0.1/path?a=1&b=2&A=keep"),
            [new KeyValuePair<string, string>("c", "3")],
            "a");
        Assert.IsFalse(stripped.Query.Contains("a=1", StringComparison.Ordinal));
        StringAssert.Contains(stripped.Query, "b=2");
        StringAssert.Contains(stripped.Query, "c=3");
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ApplyDnsAsync_ResolvesLocalhost_AndAppliesOnce()
    {
        var manager = new ClusterManager();
        var refreshed = 0;
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            ClusterManager = manager,
            RefreshReverseProxy = () => Interlocked.Increment(ref refreshed),
        };
        var state = new DiscoveryApplyState();

        await ServiceDiscovery.ApplyDnsAsync(context, "localhost", 8080, "dns-local", state, CancellationToken.None);
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("dns-local"));
        Assert.IsTrue(manager.Snapshot.Clusters["dns-local"].Destinations.Count > 0);
        Assert.AreEqual(8080, manager.Snapshot.Clusters["dns-local"].Destinations[0].Port);
        Assert.AreEqual(1, Volatile.Read(ref refreshed));

        // Same signature must not re-apply.
        await ServiceDiscovery.ApplyDnsAsync(context, "localhost", 8080, "dns-local", state, CancellationToken.None);
        Assert.AreEqual(1, Volatile.Read(ref refreshed));
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
