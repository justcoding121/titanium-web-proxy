using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class OverloadHotRestartXdsValidationTests
{
    private static string Errors(TwpConfig config) => string.Join("; ", TwpConfigValidator.Validate(config));

    private static TwpConfig WithServer(Action<ServerConfig> configure)
    {
        var server = new ServerConfig();
        configure(server);
        return new TwpConfig { Server = server };
    }

    [TestMethod]
    public void Overload_Defaults_AreValid()
    {
        Assert.AreEqual(string.Empty, Errors(WithServer(s => s.Overload = new OverloadConfig { Enabled = true })));
    }

    [TestMethod]
    public void Overload_RejectsBadIntervalRetryAndThresholds()
    {
        var errors = Errors(WithServer(s => s.Overload = new OverloadConfig
        {
            Enabled = true,
            IntervalMs = 50,
            RetryAfterSeconds = 0,
            HeapPercent = new OverloadSignalConfig { Elevated = 95, Critical = 85 },
            ThreadPoolQueue = new OverloadSignalConfig { Elevated = -1, Critical = 10 },
            ActiveConnections = new OverloadSignalConfig { Elevated = 10, Critical = 10 },
        }));

        StringAssert.Contains(errors, "server.overload.intervalMs");
        StringAssert.Contains(errors, "server.overload.retryAfterSeconds");
        StringAssert.Contains(errors, "server.overload.heapPercent.elevated must be lower");
        StringAssert.Contains(errors, "server.overload.threadPoolQueue.elevated and");
        StringAssert.Contains(errors, "server.overload.activeConnections.elevated must be lower");
    }

    [TestMethod]
    public void Overload_HeapPercentAbove100_IsRejected_AndZeroZeroMeansOff()
    {
        var tooHigh = Errors(WithServer(s => s.Overload = new OverloadConfig
        {
            Enabled = true,
            HeapPercent = new OverloadSignalConfig { Elevated = 90, Critical = 150 },
        }));
        StringAssert.Contains(tooHigh, "at most 100");

        var off = Errors(WithServer(s => s.Overload = new OverloadConfig
        {
            Enabled = true,
            ThreadPoolQueue = new OverloadSignalConfig { Elevated = 0, Critical = 0 },
        }));
        Assert.AreEqual(string.Empty, off);
    }

    [TestMethod]
    public void HotRestart_RequiresPidFileAndSaneTimeout()
    {
        var errors = Errors(WithServer(s => s.HotRestart = new HotRestartConfig
        {
            Enabled = true,
            HandoverTimeoutSeconds = 1,
        }));

        StringAssert.Contains(errors, "server.hotRestart.pidFile is required");
        StringAssert.Contains(errors, "server.hotRestart.handoverTimeoutSeconds");
    }

    [TestMethod]
    public void HotRestart_Disabled_IsNotValidated()
    {
        Assert.AreEqual(string.Empty, Errors(WithServer(s => s.HotRestart = new HotRestartConfig { Enabled = false })));
    }

    [TestMethod]
    public void HotRestart_RejectsListenersThatNeedAUdpPort()
    {
        var config = WithServer(s => s.HotRestart = new HotRestartConfig
        {
            Enabled = true,
            PidFile = Path.Combine(Path.GetTempPath(), "titanium.pid"),
        });
        config.Listeners =
        [
            new ListenerConfig { Host = "127.0.0.1", Port = 8443, DecryptSsl = true, EnableHttp3 = true },
            new ListenerConfig { Host = "127.0.0.1", Port = 8444, Type = "quic" },
        ];

        var errors = Errors(config);
        StringAssert.Contains(errors, "supports TCP listeners only: listener on port 8443");
        StringAssert.Contains(errors, "supports TCP listeners only: listener on port 8444");
    }

    [TestMethod]
    public void Xds_RequiresServer_AndPairedClientCertificate()
    {
        var errors = Errors(new TwpConfig
        {
            Xds = new XdsConfig
            {
                InitialFetchTimeoutSeconds = 0,
                Tls = new XdsTlsConfig { ClientCertPath = "client.pem" },
            },
        });

        StringAssert.Contains(errors, "xds.server is required");
        StringAssert.Contains(errors, "xds.initialFetchTimeoutSeconds");
        StringAssert.Contains(errors, "xds.tls.clientCertPath and xds.tls.clientKeyPath");
    }

    [TestMethod]
    public void Xds_WithServer_IsValid()
    {
        Assert.AreEqual(string.Empty, Errors(new TwpConfig { Xds = new XdsConfig { Server = "xds.local:18000" } }));
    }
}
