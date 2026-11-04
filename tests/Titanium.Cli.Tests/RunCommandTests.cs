using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class RunCommandTests
{
    private static readonly string[] CompressionAlgorithms = ["gzip", "br"];
    private static readonly string[] CompressionMimeTypes = ["text/*", "application/wasm"];
    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForStaticFiles()
    {
        var cfg = new TwpConfig
        {
            StaticFiles = new StaticFilesConfig { Root = "www" },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_OnlyWhenResponseCompressionIsEnabled()
    {
        var listeners = new List<ListenerConfig>
        {
            new() { Port = 8080, ForwardHost = "127.0.0.1", ForwardPort = 80 },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(new TwpConfig
        {
            Listeners = listeners,
            Server = new ServerConfig { ResponseCompression = new ResponseCompressionConfig { Enabled = true } },
        }));
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(new TwpConfig
        {
            Listeners = listeners,
            Server = new ServerConfig { ResponseCompression = new ResponseCompressionConfig { Enabled = false } },
        }));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_OnlyWhenARouteSetsGrpcWeb()
    {
        TwpConfig Config(bool grpcWeb) => new()
        {
            Listeners = [new ListenerConfig { Port = 8080 }],
            Clusters = [new Titanium.Web.Proxy.Abstractions.Clusters.ClusterConfig
            {
                Id = "c",
                Destinations = [new Titanium.Web.Proxy.Abstractions.Clusters.DestinationConfig
                {
                    Id = "d", Address = "127.0.0.1", Port = 9000,
                }],
            }],
            Routes =
            [
                new Titanium.Web.Proxy.Abstractions.Routing.RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new Titanium.Web.Proxy.Abstractions.Routing.RouteMatch { Path = "/" },
                    GrpcWeb = grpcWeb,
                },
            ],
        };

        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(Config(true)));
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(Config(false)));
    }

    [TestMethod]
    public void Load_GrpcWebYaml_BindsTheRouteFlag()
    {
        var dir = Directory.CreateTempSubdirectory("twp-grpcweb-").FullName;
        try
        {
            var path = Path.Combine(dir, "twp.yaml");
            File.WriteAllText(path, """
                listeners:
                  - port: 8080
                    host: 127.0.0.1
                routes:
                  - id: g
                    clusterId: c
                    grpcWeb: true
                    match:
                      path: /pkg.Svc/
                """);

            var loaded = Titanium.Cli.Parsers.ConfigLoader.Load(path);
            Assert.IsTrue(loaded.Config.Routes.Single().GrpcWeb);
            Assert.AreEqual(0, loaded.UnknownKeys.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void ServerConfigApplier_AppliesResponseCompression_OnlyWhenEnabled()
    {
        using var proxy = new ProxyServer(false, false, false);
        ServerConfigApplier.Apply(proxy, new ServerConfig
        {
            ResponseCompression = new ResponseCompressionConfig { Enabled = false },
        });
        Assert.IsNull(proxy.ResponseCompression);

        var enabled = new ResponseCompressionConfig { Enabled = true, MinBytes = 2048 };
        ServerConfigApplier.Apply(proxy, new ServerConfig { ResponseCompression = enabled });
        Assert.AreSame(enabled, proxy.ResponseCompression);
    }

    [TestMethod]
    public void Load_ResponseCompressionYaml_BindsServerAndRouteSettings()
    {
        var dir = Directory.CreateTempSubdirectory("twp-compress-").FullName;
        try
        {
            var path = Path.Combine(dir, "twp.yaml");
            File.WriteAllText(path, """
                listeners:
                  - port: 8080
                    host: 127.0.0.1
                server:
                  responseCompression:
                    enabled: true
                    algorithms: [gzip, br]
                    minBytes: 2048
                    maxBytes: 524288
                    level: optimal
                    mimeTypes: [text/*, application/wasm]
                """);

            var loaded = Titanium.Cli.Parsers.ConfigLoader.Load(path);
            var compression = loaded.Config.Server!.ResponseCompression!;
            Assert.IsTrue(compression.Enabled);
            CollectionAssert.AreEqual(CompressionAlgorithms, compression.Algorithms!.ToArray());
            Assert.AreEqual(2048, compression.MinBytes);
            Assert.AreEqual(524288, compression.MaxBytes);
            Assert.AreEqual("optimal", compression.Level);
            CollectionAssert.AreEqual(CompressionMimeTypes, compression.MimeTypes!.ToArray());
            
            Assert.AreEqual(0, Titanium.Web.Proxy.Configuration.TwpConfigValidator.Validate(loaded.Config).Count);
            Assert.AreEqual(0, loaded.UnknownKeys.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Load_RouteCompressionJson_BindsTheOverride()
    {
        var config = Titanium.Web.Proxy.Configuration.TwpConfigLoader.LoadJson("""
            {
              "clusters": [ { "id": "c", "destinations": [ { "id": "d", "address": "127.0.0.1", "port": 9000 } ] } ],
              "routes": [ { "id": "r", "clusterId": "c", "compression": "off", "match": { "path": "/" } } ],
              "server": { "responseCompression": { "enabled": true, "algorithms": ["br"] } }
            }
            """);

        Assert.AreEqual("off", config.Routes.Single().Compression);
        Assert.IsTrue(config.Server!.ResponseCompression!.Enabled);
        Assert.AreEqual(0, Titanium.Web.Proxy.Configuration.TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_False_ForPlainForwardHost()
    {
        var cfg = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig { Port = 8080, ForwardHost = "127.0.0.1", ForwardPort = 80 },
            ],
        };
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_OnlyWhenARouteListsAnExtAuthPolicy()
    {
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Policies = ["gate"],
        };
        var cfg = new TwpConfig
        {
            Routes = [route],
            Policies = [new RoutePolicyConfig { Name = "gate", Kind = "rateLimit", Limit = 5 }],
        };
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(cfg));

        cfg = new TwpConfig
        {
            Routes = [route],
            Policies = [new RoutePolicyConfig { Name = "gate", Kind = "extAuth", Url = "http://127.0.0.1:9/auth" }],
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));

        cfg = new TwpConfig
        {
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = route.Match }],
            Policies = [new RoutePolicyConfig { Name = "gate", Kind = "extAuth", Url = "http://127.0.0.1:9/auth" }],
        };
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(cfg), "an unused extAuth policy changes nothing");
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_OnlyWhenARouteListsAnOidcPolicy()
    {
        var oidc = new RoutePolicyConfig { Name = "login", Kind = "oidc", Issuer = "https://idp.test", ClientId = "c" };
        var match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix };

        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(new TwpConfig
        {
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = match, Policies = ["login"] }],
            Policies = [oidc],
        }), "terminate-lite cannot hold a request for a login or add the claim headers");
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(new TwpConfig
        {
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = match }],
            Policies = [oidc],
        }), "an unused oidc policy changes nothing");
    }

    [TestMethod]
    public void BuildReverseProxyOptions_CarriesGeoIpResolver_WhenProvided()
    {
        var manager = new Titanium.Web.Proxy.Clusters.ClusterManager();
        var balancer = new Titanium.Web.Proxy.Clusters.LoadBalancer();
        IGeoIpResolver resolver = new FakeGeoIpResolver();

        var options = RunCommand.BuildReverseProxyOptions([], manager, balancer, [], null, null, [], geoIpResolver: resolver);

        Assert.AreSame(resolver, options.GeoIpResolver);
    }

    private sealed class FakeGeoIpResolver : IGeoIpResolver
    {
        public bool TryGetCountry(System.Net.IPAddress address, out string countryIsoCode)
        {
            countryIsoCode = string.Empty;
            return false;
        }
    }

    [TestMethod]
    public void BuildReverseProxyOptions_CarriesThePolicyHandlers_OnlyWhenThereAreSome()
    {
        var manager = new Titanium.Web.Proxy.Clusters.ClusterManager();
        var balancer = new Titanium.Web.Proxy.Clusters.LoadBalancer();
        var handler = new FakeHandler();

        var without = RunCommand.BuildReverseProxyOptions([], manager, balancer, [], null, null, []);
        var with = RunCommand.BuildReverseProxyOptions([], manager, balancer, [], null, null, [handler]);

        Assert.IsNull(without.PolicyHandlers);
        CollectionAssert.AreEqual(new[] { handler }, with.PolicyHandlers!.ToArray());
    }

    private sealed class FakeHandler : Titanium.Web.Proxy.Abstractions.Plugins.IRoutePolicyHandler
    {
        public string Kind => "oidc";

        public ValueTask<Titanium.Web.Proxy.Abstractions.Plugins.RoutePolicyVerdict> EvaluateAsync(
            object session, RouteConfig route, RoutePolicyConfig policy, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Titanium.Web.Proxy.Abstractions.Plugins.RoutePolicyVerdict.Continue);
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForTransforms()
    {
        var cfg = new TwpConfig
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Transforms = [new TransformConfig { Kind = "PathRemovePrefix" }],
                },
            ],
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForAcmeDomain()
    {
        var cfg = new TwpConfig
        {
            Certificates = new CertificatesConfig { AcmeDomain = "example.test" },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForGrpcTranscodeEnabled()
    {
        var cfg = new TwpConfig
        {
            Plus = new PlusConfig
            {
                Enabled = true,
                Options = new Dictionary<string, string>
                {
                    ["grpc.transcode.enabled"] = "true",
                },
            },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    [TestMethod]
    public void ListenerConfig_EnableHttp2AndHttp3_FieldsExist()
    {
        var listener = new ListenerConfig
        {
            Port = 8443,
            EnableHttp2 = false,
            EnableHttp3 = true,
        };
        Assert.IsFalse(listener.EnableHttp2!.Value);
        Assert.IsTrue(listener.EnableHttp3);
    }

    [TestMethod]
    public void ShouldEnableHttp3_True_WhenUnset_OrExplicitTrue()
    {
        Assert.IsTrue(RunCommand.ShouldEnableHttp3(new TwpConfig()));
        Assert.IsTrue(RunCommand.ShouldEnableHttp3(new TwpConfig
        {
            Listeners = [new ListenerConfig { Port = 8080 }],
        }));
        Assert.IsTrue(RunCommand.ShouldEnableHttp3(new TwpConfig
        {
            Listeners = [new ListenerConfig { Port = 8080, EnableHttp3 = true }],
        }));
    }

    [TestMethod]
    public void ShouldEnableHttp3_False_WhenAnyListenerDisables()
    {
        Assert.IsFalse(RunCommand.ShouldEnableHttp3(new TwpConfig
        {
            Listeners = [new ListenerConfig { Port = 8080, EnableHttp3 = false }],
        }));
    }

    [TestMethod]
    public void ConfigNeedsRequestTimingCapture_True_ForLeastTimeCluster()
    {
        var cfg = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Algorithm = LoadBalanceAlgorithm.LeastTime,
                    Destinations =
                    [
                        new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 8080 },
                    ],
                },
            ],
        };
        Assert.IsTrue(RunCommand.ConfigNeedsRequestTimingCapture(cfg));
    }

    [TestMethod]
    public void ConfigNeedsRequestTimingCapture_False_ForRoundRobin()
    {
        var cfg = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Algorithm = LoadBalanceAlgorithm.RoundRobin,
                    Destinations =
                    [
                        new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 8080 },
                    ],
                },
            ],
        };
        Assert.IsFalse(RunCommand.ConfigNeedsRequestTimingCapture(cfg));
    }

    [TestMethod]
    public void ConfigureProxyFlags_EnablesTiming_ForLeastTimeCluster()
    {
        var cfg = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c1",
                    Algorithm = LoadBalanceAlgorithm.LeastTime,
                    Destinations =
                    [
                        new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 8080 },
                    ],
                },
            ],
        };

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Assert.IsFalse(proxy.EnableRequestTimingCapture);
        RunCommand.ConfigureProxyFlags(proxy, cfg, requiresSessionPath: false);
        Assert.IsTrue(proxy.EnableRequestTimingCapture);
    }

    [TestMethod]
    public void BuildPlusOptions_MergesControlPlane()
    {
        var plus = new PlusConfig
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig
            {
                Host = "127.0.0.1",
                Port = 9099,
                SharedSecret = "s3cret",
            },
            Options = new Dictionary<string, string> { ["extra"] = "1" },
        };

        var opts = RunCommand.BuildPlusOptions(plus);
        Assert.AreEqual("127.0.0.1", opts["controlPlane.host"]);
        Assert.AreEqual("9099", opts["controlPlane.port"]);
        Assert.AreEqual("s3cret", opts["controlPlane.sharedSecret"]);
        Assert.AreEqual("1", opts["extra"]);
    }

    [TestMethod]
    public void BuildPlusOptions_CarriesTheDashboardViewerSecret_OnlyWhenSet()
    {
        var with = RunCommand.BuildPlusOptions(new PlusConfig
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig { SharedSecret = "s3cret", DashboardViewerSecret = "viewer-secret" },
        });
        Assert.AreEqual("viewer-secret", with["controlPlane.dashboardViewerSecret"]);

        var without = RunCommand.BuildPlusOptions(new PlusConfig
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig { SharedSecret = "s3cret" },
        });
        Assert.IsFalse(without.ContainsKey("controlPlane.dashboardViewerSecret"));
    }

    [TestMethod]
    public void ApplyLogging_NoConfig_NotVerbose_UsesDebugOrReleasePosture()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        RunCommand.ApplyLogging(proxy, logging: null, verbose: false);
#if DEBUG
        Assert.IsTrue(proxy.Logging.Enabled);
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Error, proxy.Logging.MinimumLevel);
        Assert.IsTrue(proxy.Logging.EnableConsole);
#else
        Assert.IsFalse(proxy.Logging.Enabled);
#endif
    }

    [TestMethod]
    public void ApplyLogging_Verbose_ForcesDebugConsole()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        RunCommand.ApplyLogging(proxy, logging: null, verbose: true);
        Assert.IsTrue(proxy.Logging.Enabled);
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Debug, proxy.Logging.MinimumLevel);
        Assert.IsTrue(proxy.Logging.EnableConsole);
    }

    [TestMethod]
    public void ApplyLogging_YamlConfig_IsHonored()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        RunCommand.ApplyLogging(proxy, new LoggingConfig
        {
            Enabled = true,
            MinimumLevel = "Warning",
            EnableConsole = false,
            EnableFile = true,
            FilePath = "logs/cli-test.log",
        }, verbose: false);
        Assert.IsTrue(proxy.Logging.Enabled);
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, proxy.Logging.MinimumLevel);
        Assert.IsFalse(proxy.Logging.EnableConsole);
        Assert.IsTrue(proxy.Logging.EnableFile);
        Assert.AreEqual("logs/cli-test.log", proxy.Logging.FilePath);
    }

    [TestMethod]
    public void ConfigNeedsSessionPath_True_ForAccessLog()
    {
        var cfg = new TwpConfig
        {
            Server = new ServerConfig
            {
                AccessLog = new AccessLogConfig { Path = "/tmp/access.ndjson" },
            },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(cfg));
    }

    /// <summary>
    /// Pins the CLI hot path: request timing stays off unless LeastTime load balancing asks for it.
    /// Access-log timing is applied later by TryStartAccessLog, not here.
    /// </summary>
    [TestMethod]
    public void ConfigureProxyFlags_LeavesTimingCaptureOff_UnlessLeastTime()
    {
        var plain = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig { Port = 8080, ForwardHost = "127.0.0.1", ForwardPort = 9 },
            ],
        };
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Assert.IsFalse(proxy.EnableRequestTimingCapture);
        RunCommand.ConfigureProxyFlags(proxy, plain, requiresSessionPath: false);
        Assert.IsFalse(proxy.EnableRequestTimingCapture);

        var accessLog = new TwpConfig
        {
            Server = new ServerConfig
            {
                AccessLog = new AccessLogConfig { Path = "access.ndjson" },
            },
        };
        Assert.IsFalse(RunCommand.ConfigNeedsRequestTimingCapture(accessLog));
        RunCommand.ConfigureProxyFlags(proxy, accessLog, requiresSessionPath: true);
        Assert.IsFalse(proxy.EnableRequestTimingCapture);
    }
}
