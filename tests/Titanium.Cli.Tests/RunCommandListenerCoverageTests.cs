using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Service;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class RunCommandListenerCoverageTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void AddListener_CoversSocksTransparentQuicAndOverrides()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            Type = "socks",
            DecryptSsl = false,
            GenericCertificateName = "cn.test",
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
            MaxCachedConnections = 4,
            MaxConcurrentClients = 8,
        });
        Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            Type = "transparent",
            DecryptSsl = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            MaxInboundBidirectionalStreams = 16,
            MaxInboundUnidirectionalStreams = 8,
            HandshakeTimeoutSeconds = 5,
            IdleTimeoutSeconds = 30,
            GenericCertificateName = "cn.test",
        });
        Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            Type = "quic",
            DecryptSsl = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = 443,
            MaxInboundBidirectionalStreams = 32,
            MaxInboundUnidirectionalStreams = 16,
            HandshakeTimeoutSeconds = 3,
            IdleTimeoutSeconds = 20,
        });
        Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "0.0.0.0",
            Port = 0,
            DecryptSsl = false,
        });
        Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            ForwardHost = "10.0.0.1",
            ForwardPort = 8080,
            DecryptSsl = false,
        });

        Assert.IsTrue(proxy.ProxyEndPoints.Count >= 5);
        Assert.AreEqual("socks", (string)Invoke("ResolveListenerKind", new ListenerConfig { Type = "SOCKS" })!);
        Assert.AreEqual("explicit", (string)Invoke("ResolveListenerKind", new ListenerConfig())!);
        Assert.AreEqual("transparent", (string)Invoke("ResolveListenerKind", new ListenerConfig { ForwardHost = "h" })!);

        var socks = (SocksProxyEndPoint)Invoke("CreateSocksEndPoint", IPAddress.Loopback, new ListenerConfig
        {
            Port = 0, DecryptSsl = true, GenericCertificateName = "g", ForwardHost = "h", ForwardPort = 1,
        })!;
        Assert.AreEqual("h", socks.ForwardHost);

        _ = Invoke("CreateTransparentEndPoint", IPAddress.Loopback, new ListenerConfig
        {
            Port = 0, DecryptSsl = true, ForwardHost = "origin.test", ForwardPort = 443,
            GenericCertificateName = "g", MaxInboundBidirectionalStreams = 1, HandshakeTimeoutSeconds = 1,
            IdleTimeoutSeconds = 2, MaxInboundUnidirectionalStreams = 1,
        });
        _ = Invoke("CreateQuicEndPoint", IPAddress.Loopback, new ListenerConfig
        {
            Port = 0, ForwardHost = "q.test", ForwardPort = 443, GenericCertificateName = "g",
            MaxInboundBidirectionalStreams = 2, MaxInboundUnidirectionalStreams = 2,
            HandshakeTimeoutSeconds = 1, IdleTimeoutSeconds = 2,
        });

        Assert.IsTrue((bool)Invoke("ResolveListenerHttp3", new ListenerConfig { EnableHttp3 = true }, true)!);
        Assert.IsFalse((bool)Invoke("ResolveListenerHttp3", new ListenerConfig { EnableHttp3 = false }, true)!);
        Assert.IsTrue((bool)Invoke("IsTruthy",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k"] = "true" }, "k")!);
        Assert.IsFalse((bool)Invoke("IsTruthy",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k"] = "0" }, "k")!);

        Invoke("StartAcmeIfConfigured", proxy, new TwpConfig());
        Invoke("StartAcmeIfConfigured", proxy, new TwpConfig
        {
            Certificates = new CertificatesConfig { AcmeEmail = "a@b.test", AcmeDomain = "b.test" },
        });
        var middleware = new List<IProxyMiddleware>();
        Invoke("ConfigureResponseCache", proxy, middleware, new MemoryHttpResponseCache(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cache.enable"] = "true",
                ["cache.maxEntries"] = "16",
            });
        Invoke("ConfigureResponseCache", proxy, new List<IProxyMiddleware>(), new MemoryHttpResponseCache(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        RunCommand.ConfigureProxyFlags(proxy, new TwpConfig
        {
            Listeners = [new ListenerConfig { EnableHttp2 = false, EnableHttp3 = false }],
            Server = new ServerConfig { EnableHttp3 = false },
        }, requiresSessionPath: true);
        RunCommand.ConfigureProxyFlags(proxy, new TwpConfig
        {
            Listeners = [new ListenerConfig()],
            Server = new ServerConfig { EnableHttp3 = true },
        }, requiresSessionPath: false);
        RunCommand.ApplyLogging(proxy, new LoggingConfig
        {
            Enabled = true,
            MinimumLevel = "Warning",
            EnableConsole = true,
            EnableConsoleColors = false,
            EnableFile = false,
            MaxFileSizeBytes = 1024,
            MaxRolledFiles = 2,
            QueueCapacity = 8,
        }, verbose: true);
        RunCommand.ApplyLogging(proxy, logging: null, verbose: false);
        RunCommand.ReplaceRoutes([], [
            new RouteConfig
            {
                Id = "r1",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/" },
            },
        ]);
        Assert.IsTrue(RunCommand.ConfigNeedsRequestTimingCapture(new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Algorithm = LoadBalanceAlgorithm.LeastTime,
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
                },
            ],
        }));
        Assert.IsFalse(RunCommand.ShouldEnableHttp3(new TwpConfig
        {
            Listeners = [new ListenerConfig { EnableHttp3 = false }],
        }));
        _ = RunCommand.BuildPlusOptions(new PlusConfig
        {
            Options = new Dictionary<string, string> { ["k"] = "v" },
            ControlPlane = new ControlPlaneConfig
            {
                Host = "127.0.0.1",
                Port = 9,
                DashboardPort = 10,
                SharedSecret = "s",
            },
        });

        var payloadDir = Path.Combine(Path.GetTempPath(), "twp-payload-" + Guid.NewGuid().ToString("N"));
        var destDir = payloadDir + "-dest";
        Directory.CreateDirectory(Path.Combine(payloadDir, "sub"));
        File.WriteAllText(Path.Combine(payloadDir, "a.dll"), "x");
        File.WriteAllText(Path.Combine(payloadDir, "sub", "b.txt"), "y");
        try
        {
            Assert.IsNotNull(ServicePayload.DiscoverAppDirectory([Path.Combine(payloadDir, "a.dll")]));
            Assert.IsNotNull(ServicePayload.DiscoverAppDirectory([Path.Combine(payloadDir, "titanium")]));
            ServicePayload.CopyDirectory(payloadDir, destDir);
            Assert.IsTrue(File.Exists(Path.Combine(destDir, "a.dll")));
            var remapped = ServicePayload.RemapPrefix(
                [Path.Combine(payloadDir, "a.dll")], payloadDir, destDir);
            StringAssert.Contains(remapped[0], destDir);
            _ = ServicePayload.MacOsDaemonPayloadDirectory("titanium-qa");
            ServicePayload.TryDeleteDirectory(destDir);
        }
        finally
        {
            ServicePayload.TryDeleteDirectory(payloadDir);
            ServicePayload.TryDeleteDirectory(destDir);
        }
    }

    [TestMethod]
    public async Task ExecuteCoreAsync_InvalidListenerPort_ReturnsOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, """
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 0
            """);
        try
        {
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None);
            Assert.AreEqual(1, code);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static object? Invoke(string name, params object?[] args)
    {
        var methods = typeof(RunCommand).GetMethods(Priv).Where(m => m.Name == name).ToArray();
        foreach (var method in methods)
        {
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetParameterCountException)
            {
                // try next overload
            }
            catch (ArgumentException)
            {
                // try next overload
            }
        }

        throw new InvalidOperationException(name);
    }
}
