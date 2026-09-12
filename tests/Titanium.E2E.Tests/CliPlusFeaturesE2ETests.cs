using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.ControlPlane;
// GrpcFrames is internal to Plus — encode locally for the fake origin.

namespace Titanium.E2E.Tests;

[TestClass]
public class CliPlusFeaturesE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-feat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_Waf_MethodHeaderBodyAndRulesFile()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-waf2";
        var rules = Path.Combine(_tempDir, "waf-rules.json");
        await File.WriteAllTextAsync(rules, """{"denyPaths":["^/from-file"]}""");

        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["waf.enabled"] = "true",
                ["waf.denyMethods"] = "DELETE",
                ["waf.denyHeader"] = "X-Evil=bad",
                ["waf.maxBodyBytes"] = "8",
                ["waf.rulesFile"] = rules.Replace("\\", "/"),
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = DirectToListener(listen);
            await WaitControlPlaneAsync(http, control);

            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{listen}/ok")).StatusCode);

            using (var del = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{listen}/x"))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(del)).StatusCode);
            }

            using (var evil = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{listen}/y"))
            {
                evil.Headers.TryAddWithoutValidation("X-Evil", "bad");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(evil)).StatusCode);
            }

            using (var big = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{listen}/body")
            {
                Content = new StringContent("0123456789"),
            })
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(big)).StatusCode);
            }

            Assert.AreEqual(
                HttpStatusCode.Forbidden,
                (await http.GetAsync($"http://127.0.0.1:{listen}/from-file")).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_CidrDeny_BlocksNonAllowedClient()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-cidr";
        // Allow only a non-loopback documentation range — loopback traffic should be denied.
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["security.allowCidrs"] = "203.0.113.0/24",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = DirectToListener(listen);
            await WaitControlPlaneAsync(http, control);
            Assert.AreEqual(
                HttpStatusCode.Forbidden,
                (await http.GetAsync($"http://127.0.0.1:{listen}/blocked-cidr")).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_BasicAuth_AndCustomApiKeyHeader()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-basic";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["security.basicUsers"] = "alice:wonder",
                ["security.apiKeys"] = "custom-key",
                ["security.apiKeyHeader"] = "X-Custom-Key",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = DirectToListener(listen);
            await WaitControlPlaneAsync(http, control);

            Assert.AreEqual(
                HttpStatusCode.Unauthorized,
                (await http.GetAsync($"http://127.0.0.1:{listen}/noauth")).StatusCode);

            using (var basic = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{listen}/basic"))
            {
                basic.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wonder")));
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(basic)).StatusCode);
            }

            using (var key = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{listen}/key"))
            {
                key.Headers.TryAddWithoutValidation("X-Custom-Key", "custom-key");
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(key)).StatusCode);
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_Jwt_ValidAndInvalid()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "e2e" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        jwk.Kid = "e2e";
        jwk.Use = "sig";
        jwk.Alg = "RS256";
        var jwksJson = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new Dictionary<string, string?>
                {
                    ["kty"] = jwk.Kty,
                    ["use"] = jwk.Use,
                    ["alg"] = jwk.Alg,
                    ["kid"] = jwk.Kid,
                    ["n"] = jwk.N,
                    ["e"] = jwk.E,
                },
            },
        });
        using var jwksStub = new JsonHttpStub(jwksJson);
        var authority = $"http://127.0.0.1:{jwksStub.Port}";
        var jwksUrl = $"http://127.0.0.1:{jwksStub.Port}/jwks";

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-jwt";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["security.jwtAuthority"] = authority,
                ["security.jwtAudience"] = "api",
                ["security.jwksUrl"] = jwksUrl,
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(direct, control);

            Assert.AreEqual(
                HttpStatusCode.Unauthorized,
                (await http.GetAsync("http://example.invalid/nojwt")).StatusCode);

            var token = CreateSignedJwt(rsa, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), authority, "api");
            using (var ok = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/jwt"))
            {
                ok.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var resp = await http.SendAsync(ok);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, await resp.Content.ReadAsStringAsync());
            }

            using (var bad = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/jwt"))
            {
                bad.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not.a.jwt");
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);
            }
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_RateLimit_Returns429()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-rl";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["state.mode"] = "memory",
                ["state.rateLimitPerMinute"] = "2",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = DirectToListener(listen);
            await WaitControlPlaneAsync(http, control);

            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{listen}/1")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{listen}/2")).StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.GetAsync($"http://127.0.0.1:{listen}/3")).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_DiscoveryFile_WatchUpdatesCluster()
    {
        using var originA = new EchoOrigin();
        using var originB = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-watch";
        var discFile = Path.Combine(_tempDir, "clusters-watch.json");
        await File.WriteAllTextAsync(discFile, $$"""
            {"clusters":[{"id":"from-file","destinations":[{"id":"dA","address":"127.0.0.1","port":{{originA.Port}}}]}]}
            """);
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            originA.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["discovery.mode"] = "file",
                ["discovery.file"] = discFile.Replace("\\", "/"),
            });
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            await WaitSnapshotContains(http, control, secret, "dA");

            await File.WriteAllTextAsync(discFile, $$"""
                {"clusters":[{"id":"from-file","destinations":[{"id":"dB","address":"127.0.0.1","port":{{originB.Port}}}]}]}
                """);

            await WaitSnapshotContains(http, control, secret, "dB");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_DiscoveryDns_AppliesLocalhost()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-dns";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["discovery.mode"] = "dns",
                ["discovery.dnsName"] = "localhost",
                ["discovery.dnsPort"] = origin.Port.ToString(),
                ["discovery.intervalMs"] = "1000",
                ["discovery.clusterId"] = "dns-c",
            });
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            await WaitSnapshotContains(http, control, secret, "dns-c");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_DiscoveryConsulAndK8s_Stubs()
    {
        using var origin = new EchoOrigin();
        var consulJson =
            "[{\"Service\":{\"ID\":\"c1\",\"Address\":\"127.0.0.1\",\"Port\":" + origin.Port + "}}]";
        var k8sJson =
            "{\"subsets\":[{\"addresses\":[{\"ip\":\"127.0.0.1\"}],\"ports\":[{\"port\":" + origin.Port + "}]}]}";
        using var consul = new JsonHttpStub(consulJson);
        using var k8s = new JsonHttpStub(k8sJson);

        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-disc-http";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["discovery.mode"] = "consul",
                ["discovery.consulUrl"] = $"http://127.0.0.1:{consul.Port}/",
                ["discovery.intervalMs"] = "1000",
                ["discovery.clusterId"] = "consul-c",
            });
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            await WaitSnapshotContains(http, control, secret, "consul-c");
        }
        finally
        {
            harness.Dispose();
        }

        // Separate k8s process leaf
        var listen2 = CliProcessHarness.GetFreePort();
        var control2 = CliProcessHarness.GetFreePort();
        var cfg2 = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen2,
            origin.Port,
            control2,
            secret,
            new Dictionary<string, string>
            {
                ["discovery.mode"] = "k8s",
                ["discovery.k8sUrl"] = $"http://127.0.0.1:{k8s.Port}/",
                ["discovery.intervalMs"] = "1000",
                ["discovery.clusterId"] = "k8s-c",
            });
        using var harness2 = new CliProcessHarness();
        harness2.EnsurePlusDllBesideCli(copy: true);
        await harness2.StartRunAsync(cfg2, DevEnv());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control2);
            await WaitSnapshotContains(http, control2, secret, "k8s-c");
        }
        finally
        {
            harness2.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_ActiveHealth_MarksUnhealthy()
    {
        using var fail = new AlwaysFailOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-health";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            fail.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["resilience.activeHealth"] = "true",
                ["resilience.intervalMs"] = "500",
                ["resilience.unhealthyThreshold"] = "1",
                ["resilience.protocol"] = "http",
                ["resilience.path"] = "/",
                ["resilience.timeoutMs"] = "1000",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            await WaitSnapshotContains(http, control, secret, "unhealthy");
            Assert.IsTrue(fail.Hits >= 1);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_CircuitCooldown_ConfigActivates()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-cool";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["resilience.circuit.enabled"] = "true",
                ["resilience.circuit.failureThreshold"] = "2",
                ["resilience.circuit.cooldownMs"] = "1500",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv(), verbose: true);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            var combined = harness.StdOut + harness.StdErr;
            Assert.IsTrue(combined.Contains("Plus Circuit", StringComparison.OrdinalIgnoreCase), combined);
            Assert.IsTrue(
                combined.Contains("1500", StringComparison.Ordinal) ||
                combined.Contains("cooldown", StringComparison.OrdinalIgnoreCase),
                combined);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(60_000)]
    public async Task Plus_GrpcTranscode_UnaryHappyPath()
    {
        await using var origin = await StartFakeGrpcOriginAsync();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-grpc-ok";
        var pb = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GrpcTranscode", "greeter.pb");
        Assert.IsTrue(File.Exists(pb), "greeter.pb fixture missing from test output");

        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>
            {
                ["grpc.transcode.enabled"] = "true",
                ["grpc.transcode.descriptorSet"] = pb.Replace("\\", "/"),
                ["grpc.transcode.services"] = "helloworld.Greeter",
            },
            useRoutes: true);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, DevEnv());
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(direct, control);

            var response = await http.GetAsync($"http://127.0.0.1:{origin.Port}/v1/greeter/world");
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            StringAssert.Contains(body, "Hello world");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static Dictionary<string, string?> DevEnv() => new()
    {
        ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
    };

    private static HttpClient DirectToListener(int listenPort)
    {
        // ForwardHost / route listeners that accept absolute-form on the listen port.
        return new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static async Task WaitControlPlaneAsync(HttpClient http, int controlPort)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = await http.GetAsync($"http://127.0.0.1:{controlPort}/v1/snapshot");
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        throw new TimeoutException("Control plane did not become reachable");
    }

    private static async Task WaitSnapshotContains(HttpClient http, int control, string secret, string needle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string json = "";
        while (DateTime.UtcNow < deadline)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot");
            req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            var resp = await http.SendAsync(req);
            json = await resp.Content.ReadAsStringAsync();
            if (json.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"Snapshot never contained '{needle}'. Last={json}");
    }

    private static string CreateSignedJwt(RSA rsa, long exp, string iss, string aud)
    {
        var creds = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "e2e" }, SecurityAlgorithms.RsaSha256);
        var expires = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: iss,
            audience: aud,
            claims: null,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<RunningHost> StartFakeGrpcOriginAsync()
    {
        var host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app =>
            {
                app.Run(async context =>
                {
                    if (!context.Request.Path.StartsWithSegments("/helloworld.Greeter"))
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsync("not grpc path: " + context.Request.Path);
                        return;
                    }

                    var msg = Encoding.UTF8.GetBytes("Hello world");
                    var proto = new byte[2 + msg.Length];
                    proto[0] = 0x0A;
                    proto[1] = (byte)msg.Length;
                    Buffer.BlockCopy(msg, 0, proto, 2, msg.Length);
                    var framed = EncodeGrpcFrame(proto);

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/grpc";
                    context.Response.ContentLength = framed.Length;
#pragma warning disable ASP0015
                    context.Response.Headers["grpc-status"] = "0";
#pragma warning restore ASP0015
                    await context.Response.Body.WriteAsync(framed);
                });
            })
            .Build();

        await host.StartAsync();
        var port = host.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.Select(a => new Uri(a).Port).First();
        return new RunningHost(host, port);
    }

    private static byte[] EncodeGrpcFrame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[5 + payload.Length];
        framed[0] = 0; // uncompressed
        var len = payload.Length;
        framed[1] = (byte)((len >> 24) & 0xff);
        framed[2] = (byte)((len >> 16) & 0xff);
        framed[3] = (byte)((len >> 8) & 0xff);
        framed[4] = (byte)(len & 0xff);
        payload.CopyTo(framed.AsSpan(5));
        return framed;
    }

    private sealed class RunningHost(IWebHost host, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
