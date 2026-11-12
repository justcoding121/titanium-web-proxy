using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>Behavior of the glue between <c>server.admin</c> and the admin listener.</summary>
[TestClass]
public class AdminHostTests
{
    [TestMethod]
    public void TryPrepare_ReturnsNull_WhenAdminIsOff()
    {
        Assert.IsNull(AdminHost.TryPrepare(null));
        Assert.IsNull(AdminHost.TryPrepare(new AdminConfig { Enabled = false }));
    }

    [TestMethod]
    public void TryPrepare_DefaultsHostAndPort_AndCarriesTokenAndReadiness()
    {
        var setup = AdminHost.TryPrepare(new AdminConfig
        {
            Enabled = true,
            AuthToken = "tok",
            Readiness = new AdminReadinessConfig { RequireHealthyClusters = true },
            PreStopDelaySeconds = 7,
        });

        Assert.IsNotNull(setup);
        Assert.AreEqual(IPAddress.Parse(AdminServer.DefaultHost), setup.Address);
        Assert.AreEqual(0, setup.Port);
        Assert.IsTrue(setup.RequireHealthyClusters);
        Assert.AreEqual(7, setup.PreStopDelaySeconds);
        Assert.AreEqual("tok", setup.AuthToken);
    }

    [TestMethod]
    public void TryPrepare_ParsesAnExplicitLoopbackHost()
    {
        var setup = AdminHost.TryPrepare(new AdminConfig
        {
            Enabled = true,
            Host = " 127.0.0.1 ",
            Port = 9191,
        });

        Assert.IsNotNull(setup);
        Assert.AreEqual(IPAddress.Loopback, setup.Address);
        Assert.AreEqual(9191, setup.Port);
    }

    [TestMethod]
    public void TryStart_ReturnsNull_WhenSetupIsNull()
    {
        Assert.IsNull(AdminHost.TryStart(
            null,
            new ReadinessState(),
            new MetricsExposition(),
            () => AdminHost.BuildVersionInfo("hash", plusActive: false)));
    }

    [TestMethod]
    public async Task TryStart_BindsAndServesHealthz_ThenDisposeStopsIt()
    {
        var setup = AdminHost.TryPrepare(new AdminConfig
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 0,
        });
        Assert.IsNotNull(setup);

        var readiness = new ReadinessState();
        using var exposition = new MetricsExposition();
        using var server = AdminHost.TryStart(
            setup,
            readiness,
            exposition,
            () => AdminHost.BuildVersionInfo("abc", plusActive: true));
        Assert.IsNotNull(server);
        Assert.IsTrue(server.Port > 0);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("GET /healthz HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray());
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            StringAssert.Contains(text, "200");
            StringAssert.Contains(text, "ok");
        }

        var version = AdminHost.BuildVersionInfo("abc", plusActive: true);
        Assert.AreEqual("plus", version.Edition);
        Assert.AreEqual("abc", version.ConfigHash);
        Assert.IsFalse(string.IsNullOrWhiteSpace(version.Runtime));
    }

    [TestMethod]
    public void TryStart_DisposesAndRethrows_WhenThePortIsAlreadyTaken()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        var setup = AdminHost.TryPrepare(new AdminConfig
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = busyPort,
        });
        Assert.IsNotNull(setup);

        var readiness = new ReadinessState();
        using var exposition = new MetricsExposition();
        Assert.ThrowsExactly<SocketException>(() =>
            AdminHost.TryStart(
                setup,
                readiness,
                exposition,
                () => AdminHost.BuildVersionInfo("x", plusActive: false)));
    }

    [TestMethod]
    public void ComputeConfigHash_ReturnsHex_AndEmptyWhenUnreadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-admin-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "twp.yaml");
            File.WriteAllText(path, "listeners: []\n");
            var hash = AdminHost.ComputeConfigHash(path);
            Assert.AreEqual(64, hash.Length);
            Assert.AreEqual(hash.ToLowerInvariant(), hash);

            Assert.AreEqual(string.Empty, AdminHost.ComputeConfigHash(Path.Combine(dir, "missing.yaml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void BuildVersionInfo_MarksEditionAndCarriesHash()
    {
        var community = AdminHost.BuildVersionInfo("h1", plusActive: false);
        var plus = AdminHost.BuildVersionInfo("h2", plusActive: true);

        Assert.AreEqual("community", community.Edition);
        Assert.AreEqual("plus", plus.Edition);
        Assert.AreEqual("h1", community.ConfigHash);
        Assert.AreEqual("h2", plus.ConfigHash);
        Assert.IsFalse(string.IsNullOrWhiteSpace(community.Version));
        Assert.IsFalse(string.IsNullOrWhiteSpace(community.Commit));
        Assert.IsFalse(string.IsNullOrWhiteSpace(community.Runtime));
    }

    [TestMethod]
    public void ClusterView_SurfacesAnEmptySnapshot()
    {
        var clusters = new ClusterManager();
        var view = AdminHost.ClusterView(clusters);
        Assert.IsNotNull(view);
        Assert.AreEqual(0, view.ClustersWithoutHealthyDestination().Count());
    }
}
