using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
/// A misspelled or half-written policy is skipped at run time, which turns a protection into a no-op.
/// Each of these cases has to fail when the configuration loads.
/// </summary>
[TestClass]
public class PolicyAndRouteValidationTests
{
    private static ClusterConfig Cluster(string id, RetryConfig? retry = null) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = 9 }],
        Retry = retry,
    };

    private static RouteConfig Route(string id, string cluster = "c", params string[] policies) => new()
    {
        Id = id,
        ClusterId = cluster,
        Policies = policies.Length == 0 ? null : policies,
        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
    };

    private static RouteConfig RouteWithRetry(string id, RetryConfig retry) => new()
    {
        Id = id,
        ClusterId = "c",
        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        Retry = retry,
    };

    private static string Errors(TwpConfig config) => string.Join("\n", TwpConfigValidator.Validate(config));

    [TestMethod]
    public void Retry_WithBadValues_IsRejectedOnClustersAndRoutes()
    {
        var bad = new RetryConfig
        {
            Attempts = -1,
            PerTryTimeoutMilliseconds = -1,
            BackoffMilliseconds = -1,
            MaxBackoffMilliseconds = -1,
            JitterPercent = 101,
            BudgetPercent = -5,
            BudgetMinConcurrent = -1,
            StatusCodes = [404, 429, 503],
        };
        var cluster = Cluster("c", bad);
        var route = RouteWithRetry("r", bad);
        var errors = Errors(new TwpConfig { Clusters = [cluster], Routes = [route] });

        foreach (var path in new[] { "clusters.c.retry", "routes.r.retry" })
        {
            StringAssert.Contains(errors, path + ".attempts");
            StringAssert.Contains(errors, path + ".perTryTimeoutMilliseconds");
            StringAssert.Contains(errors, path + ".backoffMilliseconds");
            StringAssert.Contains(errors, path + ".maxBackoffMilliseconds");
            StringAssert.Contains(errors, path + ".jitterPercent");
            StringAssert.Contains(errors, path + ".budgetPercent");
            StringAssert.Contains(errors, path + ".budgetMinConcurrent");
            StringAssert.Contains(errors, path + ".statusCodes: 404");
        }

        Assert.IsFalse(errors.Contains("statusCodes: 429", StringComparison.Ordinal), errors);
        Assert.IsFalse(errors.Contains("statusCodes: 503", StringComparison.Ordinal), errors);

        var empty = Errors(new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes = [RouteWithRetry("r", new RetryConfig { Attempts = 1, StatusCodes = [] })],
        });
        StringAssert.Contains(empty, "statusCodes must list at least one status");
    }

    [TestMethod]
    public void Retry_WithEveryOption_IsAccepted()
    {
        var retry = new RetryConfig
        {
            Attempts = 2,
            StatusCodes = [429, 500, 503],
            PerTryTimeoutMilliseconds = 800,
            BackoffMilliseconds = 50,
            MaxBackoffMilliseconds = 500,
            JitterPercent = 10,
            HonorRetryAfter = true,
            BudgetPercent = 20,
            BudgetMinConcurrent = 5,
        };
        var config = new TwpConfig
        {
            Clusters = [Cluster("c", retry)],
            Routes = [RouteWithRetry("r", retry)],
        };
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }

    [TestMethod]
    public void Route_NamingAnUndefinedPolicy_IsRejected()
    {
        var config = new TwpConfig { Clusters = [Cluster("c")], Routes = [Route("r", "c", "ghost")] };
        StringAssert.Contains(Errors(config), "unknown policy 'ghost'");
    }

    [TestMethod]
    public void Policies_WithUnknownKindOrMissingParameters_AreRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "a", Kind = "rateLimt" },
                new RoutePolicyConfig { Name = "b", Kind = "apiKey", HeaderName = "X-Key" },
                new RoutePolicyConfig { Name = "c1", Kind = "maxRequestBodySize" },
                new RoutePolicyConfig { Name = "d", Kind = "requireHeader" },
                new RoutePolicyConfig { Name = "e", Kind = "timeout" },
                new RoutePolicyConfig { Name = "f", Kind = "rateLimit", Limit = 0, WindowSeconds = 0 },
                new RoutePolicyConfig { Name = "g", Kind = "ipAllowDeny" },
                new RoutePolicyConfig { Name = "h", Kind = "ipAllowDeny", Deny = "10.0.0.0/8, bogus" },
                new RoutePolicyConfig { Name = "a", Kind = "cors" },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "unknown kind 'rateLimt'");
        StringAssert.Contains(errors, "'b' (apiKey) needs headerName and headerValue");
        StringAssert.Contains(errors, "'c1' (maxRequestBodySize)");
        StringAssert.Contains(errors, "'d' (requireHeader) needs headerName");
        StringAssert.Contains(errors, "'e' (timeout)");
        StringAssert.Contains(errors, "limit must be >= 1");
        StringAssert.Contains(errors, "windowSeconds must be >= 1");
        StringAssert.Contains(errors, "'g' (ipAllowDeny) needs allow or deny");
        StringAssert.Contains(errors, "'bogus' is not an address or CIDR prefix");
        StringAssert.Contains(errors, "Duplicate policy name 'a'");
    }

    [TestMethod]
    public void ExtAuthPolicy_RequiresAnAbsoluteHttpUrl_AndSaneLimitsAndHeaders()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "none", Kind = "extAuth" },
                new RoutePolicyConfig { Name = "rel", Kind = "extAuth", Url = "/auth" },
                new RoutePolicyConfig { Name = "ftp", Kind = "extAuth", Url = "ftp://auth.test/" },
                new RoutePolicyConfig { Name = "creds", Kind = "extAuth", Url = "http://user:pw@auth.test/" },
                new RoutePolicyConfig
                {
                    Name = "limits",
                    Kind = "extAuth",
                    Url = "http://auth.test/",
                    Method = "TRACE",
                    TimeoutMilliseconds = 60_001,
                    CacheSeconds = -1,
                    CacheMaxEntries = 0,
                },
                new RoutePolicyConfig
                {
                    Name = "zero",
                    Kind = "extAuth",
                    Url = "http://auth.test/",
                    TimeoutMilliseconds = 0,
                },
                new RoutePolicyConfig
                {
                    Name = "headers",
                    Kind = "extAuth",
                    Url = "http://auth.test/",
                    SendHeaders = ["Authorization", "X-Original-Uri", "bad name"],
                    CopyResponseHeaders = ["X-User", "Transfer-Encoding"],
                    DenyResponseHeaders = ["Location", "Content-Length"],
                },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "'none' (extAuth) needs url");
        StringAssert.Contains(errors, "'rel' (extAuth) needs url");
        StringAssert.Contains(errors, "'ftp' (extAuth) needs url");
        StringAssert.Contains(errors, "'creds' (extAuth) needs url");
        StringAssert.Contains(errors, "method 'TRACE'");
        StringAssert.Contains(errors, "'limits' (extAuth) timeoutMilliseconds must be 1-60000");
        StringAssert.Contains(errors, "'zero' (extAuth) timeoutMilliseconds must be 1-60000");
        StringAssert.Contains(errors, "cacheSeconds must be >= 0");
        StringAssert.Contains(errors, "cacheMaxEntries must be 1-1000000");
        StringAssert.Contains(errors, "sendHeaders entry 'X-Original-Uri' is set by the proxy");
        StringAssert.Contains(errors, "sendHeaders entry 'bad name' is not a header name");
        StringAssert.Contains(errors, "copyResponseHeaders entry 'Transfer-Encoding' is a connection-level header");
        StringAssert.Contains(errors, "denyResponseHeaders entry 'Content-Length' is a connection-level header");
    }

    [TestMethod]
    public void ExtAuthPolicy_WithDefaults_IsAccepted()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes = [Route("r", "c", "gate")],
            Policies =
            [
                new RoutePolicyConfig { Name = "gate", Kind = "extAuth", Url = "https://auth.test:8443/check" },
                new RoutePolicyConfig
                {
                    Name = "full",
                    Kind = "extAuth",
                    Url = "http://auth.test/",
                    Method = "get",
                    SendHeaders = ["Authorization", "Cookie", "X-Api-Key"],
                    CopyResponseHeaders = ["X-User", "X-Email"],
                    DenyResponseHeaders = ["Location", "Set-Cookie", "WWW-Authenticate"],
                    TimeoutMilliseconds = 60_000,
                    FailOpen = true,
                    CacheSeconds = 30,
                    CacheMaxEntries = 100,
                },
            ],
        };

        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void RateLimitV2_RejectsUnknownKeysAlgorithmsAndBadBounds()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "k1", Kind = "rateLimit", Key = "ip" },
                new RoutePolicyConfig { Name = "k2", Kind = "rateLimit", Key = "header:" },
                new RoutePolicyConfig { Name = "k3", Kind = "rateLimit", Key = "cookie:bad name" },
                new RoutePolicyConfig { Name = "a1", Kind = "rateLimit", Algorithm = "leakyBucket" },
                new RoutePolicyConfig { Name = "b1", Kind = "rateLimit", Algorithm = "tokenBucket", Burst = 0 },
                new RoutePolicyConfig { Name = "m1", Kind = "rateLimit", MaxKeys = 999 },
                new RoutePolicyConfig { Name = "m2", Kind = "rateLimit", MaxKeys = 10_000_001 },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "'k1' (rateLimit) key 'ip'");
        StringAssert.Contains(errors, "'k2' (rateLimit) key 'header:'");
        StringAssert.Contains(errors, "'k3' (rateLimit) key 'cookie:bad name'");
        StringAssert.Contains(errors, "'a1' (rateLimit) algorithm 'leakyBucket'");
        StringAssert.Contains(errors, "'b1' (rateLimit) burst must be >= 1");
        StringAssert.Contains(errors, "'m1' (rateLimit) maxKeys must be 1000-10000000");
        StringAssert.Contains(errors, "'m2' (rateLimit) maxKeys must be 1000-10000000");
    }

    [TestMethod]
    public void RateLimitV2_AcceptsEveryKeyTypeAndAlgorithm()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "p1", Kind = "rateLimit", Key = "client", Algorithm = "fixedWindow" },
                new RoutePolicyConfig { Name = "p2", Kind = "rateLimit", Key = "forwardedClient", Algorithm = "tokenBucket", Burst = 1 },
                new RoutePolicyConfig { Name = "p3", Kind = "rateLimit", Key = "header:X-Api-Key", MaxKeys = 1000 },
                new RoutePolicyConfig { Name = "p4", Kind = "rateLimit", Key = "cookie:sid", MaxKeys = 10_000_000, EmitHeaders = true },
                new RoutePolicyConfig { Name = "p5", Kind = "rateLimit", Key = "route", Limit = 5, WindowSeconds = 10 },
                new RoutePolicyConfig { Name = "p6", Kind = "rateLimit", Scope = "fleet", FleetFailMode = "local", LocalShare = 0.5 },
                new RoutePolicyConfig { Name = "p7", Kind = "rateLimit", Scope = "node" },
            ],
        };

        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void RateLimitFleet_RejectsInvalidScopeFailModeAndLocalShare()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "s1", Kind = "rateLimit", Scope = "cluster" },
                new RoutePolicyConfig { Name = "f1", Kind = "rateLimit", Scope = "fleet", FleetFailMode = "panic" },
                new RoutePolicyConfig { Name = "l1", Kind = "rateLimit", Scope = "fleet", LocalShare = 0 },
                new RoutePolicyConfig { Name = "l2", Kind = "rateLimit", Scope = "fleet", LocalShare = 1.5 },
                new RoutePolicyConfig { Name = "n1", Kind = "rateLimit", Scope = "node", FleetFailMode = "local" },
                new RoutePolicyConfig { Name = "n2", Kind = "rateLimit", LocalShare = 0.5 },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "'s1' (rateLimit) scope");
        StringAssert.Contains(errors, "'f1' (rateLimit) fleetFailMode");
        StringAssert.Contains(errors, "'l1' (rateLimit) localShare");
        StringAssert.Contains(errors, "'l2' (rateLimit) localShare");
        StringAssert.Contains(errors, "'n1' (rateLimit) fleetFailMode requires scope: fleet");
        StringAssert.Contains(errors, "'n2' (rateLimit) localShare requires scope: fleet");
    }

    [TestMethod]
    public void ForwardedHeadersTrustedProxies_RejectsEntriesThatWouldTrustNobody()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Server = new ServerConfig
            {
                ForwardedHeaders = new Titanium.Web.Proxy.Abstractions.Routing.ForwardedHeadersConfig
                {
                    TrustedProxies = ["10.0.0.0/8", "::1", "10.0.0.0/40", "not-an-address"],
                },
            },
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "trustedProxies entry '10.0.0.0/40'");
        StringAssert.Contains(errors, "trustedProxies entry 'not-an-address'");
        Assert.IsFalse(errors.Contains("'10.0.0.0/8'") || errors.Contains("'::1'"), errors);
    }

    [TestMethod]
    public void WellFormedPolicies_AreAccepted()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Policies =
            [
                new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Key", HeaderValue = "v" },
                new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 5, WindowSeconds = 10 },
                new RoutePolicyConfig { Name = "ip", Kind = "ipAllowDeny", Allow = "10.0.0.0/8,2001:db8::/32" },
                new RoutePolicyConfig { Name = "cap", Kind = "maxRequestBodySize", MaxRequestBodyBytes = 10 },
                new RoutePolicyConfig { Name = "t", Kind = "timeout", TimeoutSeconds = 5 },
                new RoutePolicyConfig { Name = "cors", Kind = "cors" },
                new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 3 },
                new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant" },
            ],
            Routes = [Route("r", "c", "key", "rate", "ip", "cap", "t", "cors", "slots", "need")],
        };

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }

    [TestMethod]
    public void Regexes_ThatDoNotCompile_AreRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                new RouteConfig
                {
                    Id = "path",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "([", PathKind = PathMatchKind.Regex },
                },
                new RouteConfig
                {
                    Id = "tpl",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/u/{id:regex((?=a)a)}", PathKind = PathMatchKind.Template },
                },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "Route 'path' has a path regex that does not compile");
        StringAssert.Contains(errors, "Route 'tpl' template constraint");
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(65536, true)]
    [DataRow(1_048_576, true)]
    [DataRow(1_048_577, false)]
    [DataRow(int.MaxValue, false)]
    public void MirrorMaxBodyBytes_IsBounded(int maxBodyBytes, bool valid)
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                new RouteConfig
                {
                    Id = "m",
                    ClusterId = "c",
                    Mirror = new MirrorConfig { ClusterId = "c", MaxBodyBytes = maxBodyBytes },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var errors = Errors(config);
        Assert.AreEqual(valid, !errors.Contains("maxBodyBytes", StringComparison.Ordinal), errors);
    }

    [TestMethod]
    public void Mirror_AndRedirect_AreChecked()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                new RouteConfig
                {
                    Id = "m",
                    ClusterId = "c",
                    Mirror = new MirrorConfig { ClusterId = "nope", SamplePercent = 150 },
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
                new RouteConfig
                {
                    Id = "rd",
                    ClusterId = "c",
                    Redirect = new RedirectConfig { Status = 200, Host = "x.test" },
                    Match = new RouteMatch { Path = "/r", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "mirrors to unknown cluster 'nope'");
        StringAssert.Contains(errors, "samplePercent must be 0-100");
        StringAssert.Contains(errors, "redirect status 200 is not a 3xx code");
    }

    private static TwpConfig WithCompression(ResponseCompressionConfig compression, params RouteConfig[] routes) => new()
    {
        Clusters = [Cluster("c")],
        Routes = routes,
        Server = new ServerConfig { ResponseCompression = compression },
    };

    [TestMethod]
    public void ResponseCompression_Defaults_AreValid_AndWarnAboutUnsupportedPaths()
    {
        var config = WithCompression(new ResponseCompressionConfig { Enabled = true });

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
        var warning = TwpConfigValidator.CollectWarnings(config)
            .Single(w => w.Contains("responseCompression", StringComparison.Ordinal));
        StringAssert.Contains(warning, "HTTP/2-to-HTTP/2");
        StringAssert.Contains(warning, "HTTP/3");
        StringAssert.Contains(warning, "HTTP/1.1-to-HTTP/2");
    }

    [TestMethod]
    public void ResponseCompression_Disabled_HasNoWarning()
    {
        var config = WithCompression(new ResponseCompressionConfig { Enabled = false });
        Assert.IsFalse(TwpConfigValidator.CollectWarnings(config)
            .Any(w => w.Contains("responseCompression", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResponseCompression_BadValues_AreRejected()
    {
        var config = WithCompression(new ResponseCompressionConfig
        {
            Enabled = true,
            Algorithms = ["br", "zstd"],
            MinBytes = 0,
            MaxBytes = 2_000_000,
            Level = "ludicrous",
            MimeTypes = ["text/*", "html"],
        });

        var errors = Errors(config);
        StringAssert.Contains(errors, "server.responseCompression.algorithms entry 'zstd'");
        StringAssert.Contains(errors, "server.responseCompression.minBytes");
        StringAssert.Contains(errors, "server.responseCompression.maxBytes");
        StringAssert.Contains(errors, "server.responseCompression.level 'ludicrous'");
        StringAssert.Contains(errors, "server.responseCompression.mimeTypes entry 'html'");
    }

    [TestMethod]
    public void ResponseCompression_MinAboveMax_IsRejected()
    {
        var config = WithCompression(new ResponseCompressionConfig { Enabled = true, MinBytes = 5000, MaxBytes = 4000 });
        StringAssert.Contains(Errors(config), "minBytes must not exceed maxBytes");
    }

    [DataTestMethod]
    [DataRow("buffered")]
    [DataRow("streaming")]
    [DataRow("Streaming")]
    public void ResponseCompression_KnownModes_AreValid(string mode)
    {
        var config = WithCompression(new ResponseCompressionConfig { Enabled = true, Mode = mode });
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }

    [TestMethod]
    public void ResponseCompression_UnknownMode_IsRejected()
    {
        var config = WithCompression(new ResponseCompressionConfig { Enabled = true, Mode = "chunked" });
        StringAssert.Contains(Errors(config), "server.responseCompression.mode 'chunked' is unknown");
    }

    private static TwpConfig WithRequestDecompression(RequestDecompressionConfig decompression) => new()
    {
        Clusters = [Cluster("c")],
        Routes = [],
        Server = new ServerConfig { RequestDecompression = decompression },
    };

    [TestMethod]
    public void RequestDecompression_Defaults_AreValid_AndWarnAboutScope()
    {
        var config = WithRequestDecompression(new RequestDecompressionConfig { Enabled = true });

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
        var warning = TwpConfigValidator.CollectWarnings(config)
            .Single(w => w.Contains("requestDecompression", StringComparison.Ordinal));
        StringAssert.Contains(warning, "HTTP/1.x");
    }

    [TestMethod]
    public void RequestDecompression_Disabled_HasNoWarning()
    {
        var config = WithRequestDecompression(new RequestDecompressionConfig { Enabled = false });
        Assert.IsFalse(TwpConfigValidator.CollectWarnings(config)
            .Any(w => w.Contains("requestDecompression", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RequestDecompression_OutOfRangeLimits_AreRejected()
    {
        var config = WithRequestDecompression(new RequestDecompressionConfig
        {
            Enabled = true,
            MaxDecompressedBytes = RequestDecompressionConfig.MaxAllowedDecompressedBytes + 1,
            MaxRatio = 0,
        });

        var errors = Errors(config);
        StringAssert.Contains(errors, "server.requestDecompression.maxDecompressedBytes");
        StringAssert.Contains(errors, "server.requestDecompression.maxRatio");
    }

    [TestMethod]
    public void RouteCompression_OnlyOffIsAccepted()
    {
        RouteConfig Compressed(string id, string? mode) => new()
        {
            Id = id,
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Compression = mode,
        };

        var ok = new TwpConfig { Clusters = [Cluster("c")], Routes = [Compressed("a", "off"), Compressed("b", null)] };
        Assert.AreEqual(0, TwpConfigValidator.Validate(ok).Count, Errors(ok));

        var bad = new TwpConfig { Clusters = [Cluster("c")], Routes = [Compressed("a", "gzip")] };
        StringAssert.Contains(Errors(bad), "Route 'a' compression 'gzip' is unknown");
    }

    private static RouteConfig GrpcWebRoute(string id, bool grpcWeb, RedirectConfig? redirect = null,
        DirectResponseConfig? direct = null) => new()
    {
        Id = id,
        ClusterId = "c",
        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        GrpcWeb = grpcWeb,
        Redirect = redirect,
        DirectResponse = direct,
    };

    [TestMethod]
    public void GrpcWeb_OnAnOriginRoute_IsValid_AndWarnsAboutScope()
    {
        var config = new TwpConfig { Clusters = [Cluster("c")], Routes = [GrpcWebRoute("g", true)] };

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
        var warning = TwpConfigValidator.CollectWarnings(config)
            .Single(w => w.Contains("grpcWeb", StringComparison.Ordinal));
        StringAssert.Contains(warning, "grpc-web-text");
        StringAssert.Contains(warning, "HTTP/2 origin");
    }

    [TestMethod]
    public void GrpcWeb_Off_HasNoWarning()
    {
        var config = new TwpConfig { Clusters = [Cluster("c")], Routes = [GrpcWebRoute("g", false)] };
        Assert.IsFalse(TwpConfigValidator.CollectWarnings(config)
            .Any(w => w.Contains("grpcWeb", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GrpcWeb_WithRedirectOrDirectResponse_IsRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Routes =
            [
                GrpcWebRoute("a", true, redirect: new RedirectConfig { Host = "example.test" }),
                GrpcWebRoute("b", true, direct: new DirectResponseConfig { Status = 200, Body = "x" }),
            ],
        };

        var errors = Errors(config);
        StringAssert.Contains(errors, "Route 'a' grpcWeb needs an origin");
        StringAssert.Contains(errors, "Route 'b' grpcWeb needs an origin");
    }

    [TestMethod]
    public void GrpcWeb_BindsFromJson()
    {
        var config = TwpConfigLoader.LoadJson("""
            {
              "clusters": [ { "id": "c", "destinations": [ { "id": "d", "address": "127.0.0.1", "port": 9000 } ] } ],
              "routes": [
                { "id": "g", "clusterId": "c", "grpcWeb": true, "match": { "path": "/pkg.Svc/" } },
                { "id": "p", "clusterId": "c", "match": { "path": "/" } }
              ]
            }
            """);

        Assert.IsTrue(config.Routes.Single(r => r.Id == "g").GrpcWeb);
        Assert.IsFalse(config.Routes.Single(r => r.Id == "p").GrpcWeb);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }
}
