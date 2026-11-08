using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class ListenerOverlapValidationTests
{
    [TestMethod]
    public void DuplicateExplicitHostAndPort_IsRejected()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("127.0.0.1", 8000, "explicit"),
            Listener("127.0.0.1", 8000, "explicit")));

        Assert.IsTrue(errors.Any(error => error.Contains("listeners[0]") && error.Contains("listeners[1]") && error.Contains("tcp")));
    }

    [TestMethod]
    public void SamePortOnTcpAndUdp_IsAccepted()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("127.0.0.1", 443, "explicit"),
            Udp("127.0.0.1", 443)));

        Assert.IsFalse(errors.Any(error => error.Contains("conflicts")));
    }

    [TestMethod]
    public void TransparentTcpAndQuicOnTheSamePort_IsAccepted()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("127.0.0.1", 443, "transparent"),
            Listener("127.0.0.1", 443, "quic")));

        Assert.IsFalse(errors.Any(error => error.Contains("conflicts")));
    }

    [TestMethod]
    public void WildcardOverlapsASpecificAddress()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("0.0.0.0", 8000, "explicit"),
            Listener("127.0.0.1", 8000, "explicit")));

        Assert.IsTrue(errors.Any(error => error.Contains("overlapping addresses")));
    }

    [TestMethod]
    public void LocalhostOverlapsLoopback()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("localhost", 8000, "explicit"),
            Listener("127.0.0.1", 8000, "explicit")));

        Assert.IsTrue(errors.Any(error => error.Contains("overlapping addresses")));
    }

    [TestMethod]
    public void PortZero_DoesNotConflict()
    {
        var errors = TwpConfigValidator.Validate(Config(
            Listener("127.0.0.1", 0, "explicit"),
            Listener("127.0.0.1", 0, "explicit")));

        Assert.IsFalse(errors.Any(error => error.Contains("conflicts")));
    }

    [TestMethod]
    public void MetricsPort_ConflictsWithATcpListener()
    {
        var config = Config(Listener("127.0.0.1", 9464, "explicit"));
        config.Server = new ServerConfig
        {
            Metrics = new MetricsConfig { Enabled = true },
        };

        var errors = TwpConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("server.metrics port 9464") && error.Contains("listeners[0]")));
    }

    [TestMethod]
    public void MetricsDisabled_DoesNotConflict()
    {
        var config = Config(Listener("127.0.0.1", 9464, "explicit"));
        config.Server = new ServerConfig
        {
            Metrics = new MetricsConfig { Enabled = false, Port = 9464 },
        };

        var errors = TwpConfigValidator.Validate(config);

        Assert.IsFalse(errors.Any(error => error.Contains("server.metrics")));
    }

    [TestMethod]
    public void ControlPlanePort_ConflictsOnlyWhenPlusIsEnabled()
    {
        var off = Config(Listener("127.0.0.1", 9080, "explicit"));
        off.Plus = new PlusConfig { Enabled = false };
        Assert.IsFalse(TwpConfigValidator.Validate(off).Any(error => error.Contains("plus.controlPlane")));

        var on = Config(Listener("127.0.0.1", 9080, "explicit"));
        on.Plus = new PlusConfig { Enabled = true };
        Assert.IsTrue(TwpConfigValidator.Validate(on).Any(error => error.Contains("plus.controlPlane port 9080")));
    }

    private static TwpConfig Config(params ListenerConfig[] listeners) =>
        new() { Listeners = listeners.ToList() };

    private static ListenerConfig Listener(string host, int port, string type) =>
        new() { Host = host, Port = port, Type = type };

    private static ListenerConfig Udp(string host, int port) =>
        new()
        {
            Host = host,
            Port = port,
            Type = "udp",
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
        };
}
