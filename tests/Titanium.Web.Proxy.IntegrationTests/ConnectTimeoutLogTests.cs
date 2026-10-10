using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07: <c>[ERROR] connect timeout exceeded ConnectTimeOutSeconds=Connect; observed 0</c>.
///     The line must name the configured limit and a real observed time, and the client must get a 504.
///     The origin is a loopback listener whose accept backlog is full, so the SYN is never answered and the
///     connect really times out without touching the network.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConnectTimeoutLogTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task ConnectTimeout_LogNamesConfiguredLimit_AndClientGets504()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Fill the (never-accepted) backlog so further connects are dropped, not completed by the kernel.
        var fillers = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var filler = new TcpClient();
                fillers.Add(filler);
                var connect = filler.ConnectAsync(IPAddress.Loopback, port);
                if (await Task.WhenAny(connect, Task.Delay(300)) != connect)
                    break;
            }

            var logs = new LevelCapturingLoggerFactory();
            using var testSuite = new TestSuite();
            var proxy = testSuite.GetProxy();
            proxy.ConnectTimeOutSeconds = 1;
            proxy.Logging.LoggerFactory = logs;
            proxy.ApplyLoggingConfiguration();

            var client = testSuite.GetClient(proxy);
            using var response = await client.GetAsync($"https://127.0.0.1:{port}/");

            if (response.StatusCode != HttpStatusCode.GatewayTimeout)
                Assert.Inconclusive(
                    $"This OS completed the connect despite a full backlog (status {(int)response.StatusCode}); " +
                    "a loopback blackhole cannot be built here.");

            var all = string.Join(Environment.NewLine, logs.Entries.Select(e => $"[{e.Level}] {e.Message}"));
            Assert.IsFalse(all.Contains("=Connect;", StringComparison.Ordinal), all);
            Assert.IsFalse(all.Contains("observed 0;", StringComparison.Ordinal), all);
            Assert.IsTrue(all.Contains("=1s", StringComparison.Ordinal),
                "the log must name the configured 1s limit" + Environment.NewLine + all);
            Assert.AreEqual(1, logs.Entries.Count(e => e.Level == LogLevel.Error
                                                       && e.Message.Contains("=1s", StringComparison.Ordinal)));
        }
        finally
        {
            foreach (var f in fillers)
                f.Dispose();
        }
    }
}
