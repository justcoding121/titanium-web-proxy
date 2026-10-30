using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Tests.Lua;
using Titanium.Web.Proxy.Scripting.Tests.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Lua.LuaTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests;

/// <summary>
/// Runs the scripts under <c>examples/</c> through the real engines, so a recipe that stops working (an API rename, a changed
/// default) fails here instead of in an operator's configuration. Tests are inconclusive when the native libraries are absent.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExampleScriptsTests
{
    private static readonly string ExamplesDir = FindExamples();

    private static string FindExamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples");
            if (Directory.Exists(Path.Combine(candidate, "scripting")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("examples/scripting was not found above " + AppContext.BaseDirectory);
    }

    private static string Recipe(string name) => Path.Combine(ExamplesDir, "scripting", name);

    private static LuaPlugin CreateFile(string file, string name, IReadOnlyDictionary<string, string>? settings = null,
        string? bodyMode = null, IReadOnlyList<string>? calloutHosts = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Create(new PluginConfig
        {
            Name = name,
            Kind = PluginKinds.Lua,
            File = file,
            Settings = settings,
            BodyMode = bodyMode,
            CalloutHosts = calloutHosts,
            Limits = new PluginLimitsConfig { PoolSize = 1 },
        }, logger);

    private static ScriptPhaseContext Request(string ip = "10.1.2.3", params KeyValuePair<string, string>[] headers) => new()
    {
        Phase = ScriptPhases.RequestHeaders,
        Method = "GET",
        Path = "/api/things",
        Host = "example.test",
        RouteId = "r1",
        ClusterId = "c1",
        Headers = headers,
        Items = new Dictionary<string, object?>(),
        ClientEndPoint = new IPEndPoint(IPAddress.Parse(ip), 5555),
    };

    private static string Body(ScriptPhaseResult result) => Encoding.UTF8.GetString(result.LocalResponse!.Body!);

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name) =>
        headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    // ---- Lua header gate -----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task LuaHeaderGate_DeniesWithoutTheHeader_AndStampsWithIt()
    {
        var file = Path.Combine(ExamplesDir, "Titanium.Web.Proxy.Examples.LuaHeaderGate", "header_gate.lua");
        using var plugin = CreateFile(file, "header-gate");

        var denied = await plugin.OnRequestHeadersAsync(Request(), default);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(403, denied.LocalResponse!.Status);
        Assert.AreEqual("missing X-Api-Client", Body(denied));

        var allowed = await plugin.OnRequestHeadersAsync(Request(headers: [H("X-Api-Client", "demo")]), default);
        Assert.AreEqual(ScriptAction.Continue, allowed.Action);
        Assert.AreEqual("header-gate", Header(allowed.SetHeaders, "X-Route-Plugin"));
    }

    [TestMethod]
    public async Task LuaHeaderGate_ReadsBothHeaderNamesFromSettings()
    {
        var file = Path.Combine(ExamplesDir, "Titanium.Web.Proxy.Examples.LuaHeaderGate", "header_gate.lua");
        using var plugin = CreateFile(file, "header-gate",
            new Dictionary<string, string> { ["requiredHeader"] = "X-Key", ["stampHeader"] = "X-Stamp" });

        var denied = await plugin.OnRequestHeadersAsync(Request(headers: [H("X-Api-Client", "demo")]), default);
        Assert.AreEqual("missing X-Key", Body(denied));

        var allowed = await plugin.OnRequestHeadersAsync(Request(headers: [H("X-Key", "1")]), default);
        Assert.AreEqual("header-gate", Header(allowed.SetHeaders, "X-Stamp"));
    }

    // ---- Wasm header gate ----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task WasmHeaderGate_Wat_DeniesWithoutTheHeader_AndStampsWithIt()
    {
        var file = Path.Combine(ExamplesDir, "Titanium.Web.Proxy.Examples.WasmHeaderGate", "header_gate.wat");
        using var plugin = WasmTestSupport.Create(WasmTestSupport.Config(file, name: "header-gate"));

        var denied = await plugin.OnRequestHeadersAsync(
            WasmTestSupport.Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()), default);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(403, denied.LocalResponse!.Status);
        Assert.AreEqual("missing x-api-client", Body(denied));

        var allowed = await plugin.OnRequestHeadersAsync(
            WasmTestSupport.Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(),
                [WasmTestSupport.H("X-Api-Client", "demo")]), default);
        Assert.AreEqual(ScriptAction.Continue, allowed.Action);
        Assert.AreEqual("header-gate", Header(allowed.SetHeaders, "x-route-plugin"));
    }

    // ---- recipes -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RateLimit_DeniesPastTheLimit_PerClient()
    {
        using var plugin = CreateFile(Recipe("rate_limit.lua"), "rate-limit", new Dictionary<string, string>
        {
            ["sharedDicts"] = "rate:100",
            ["limit"] = "2",
            ["windowSeconds"] = "30",
        });
        var client = "10.77." + Random.Shared.Next(1, 250) + "." + Random.Shared.Next(1, 250);

        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnRequestHeadersAsync(Request(client), default)).Action);
        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnRequestHeadersAsync(Request(client), default)).Action);
        var third = await plugin.OnRequestHeadersAsync(Request(client), default);
        Assert.AreEqual(ScriptAction.Deny, third.Action);
        Assert.AreEqual(429, third.LocalResponse!.Status);
        Assert.AreEqual("30", third.LocalResponse.Headers!.Single(h => h.Key == "retry-after").Value);

        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnRequestHeadersAsync(Request("10.78.0.1"), default)).Action,
            "another client has its own counter");
    }

    [TestMethod]
    public async Task BodyRewrite_ReplacesTextInTextBodies_Only()
    {
        using var plugin = CreateFile(Recipe("body_rewrite.lua"), "body-rewrite", new Dictionary<string, string>
        {
            ["from"] = "http://internal.example",
            ["to"] = "https://www.example.com",
        }, bodyMode: "buffered");

        ScriptPhaseContext Response(string contentType, string body) => new()
        {
            Phase = ScriptPhases.ResponseBody,
            Method = "GET",
            Path = "/",
            Host = "example.test",
            StatusCode = 200,
            Headers = [H("content-type", contentType)],
            Chunk = Encoding.UTF8.GetBytes(body),
            IsLastChunk = true,
            Items = new Dictionary<string, object?>(),
        };

        var html = await plugin.OnResponseBodyAsync(
            Response("text/html", "<a href=\"http://internal.example/a\">x</a> http://internal.example"), default);
        Assert.AreEqual("<a href=\"https://www.example.com/a\">x</a> https://www.example.com",
            Encoding.UTF8.GetString(html.ReplaceBody!));

        var image = await plugin.OnResponseBodyAsync(Response("image/png", "http://internal.example"), default);
        Assert.IsNull(image.ReplaceBody);

        var untouched = await plugin.OnResponseBodyAsync(Response("text/plain", "nothing to do"), default);
        Assert.IsNull(untouched.ReplaceBody);
    }

    [TestMethod]
    public async Task RequestLogger_WritesOneJsonLinePerRequest()
    {
        var logger = new ListLogger();
        using var plugin = CreateFile(Recipe("request_logger.lua"), "request-logger", logger: logger);

        await plugin.OnLogAsync(new ScriptPhaseContext
        {
            Phase = ScriptPhases.Log,
            Method = "POST",
            Path = "/orders",
            Host = "shop.test",
            StatusCode = 201,
            RouteId = "orders",
            ClusterId = "backend",
            ClientEndPoint = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 5555),
            Elapsed = TimeSpan.FromMilliseconds(12),
            Items = new Dictionary<string, object?>(),
        }, default);

        var line = logger.Lines.Single(l => l.Contains("[lua:request-logger]", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(line[(line.IndexOf('{'))..]);
        Assert.AreEqual("POST", json.RootElement.GetProperty("method").GetString());
        Assert.AreEqual("/orders", json.RootElement.GetProperty("path").GetString());
        Assert.AreEqual(201, json.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual("orders", json.RootElement.GetProperty("route").GetString());
    }

    [TestMethod]
    public async Task JwtCheck_AcceptsAGoodToken_AndRefusesTheRest()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "oct", kid = "k1", alg = "HS256", k = Base64Url(key) } },
        });
        using var plugin = CreateFile(Recipe("jwt_check.lua"), "jwt-check", new Dictionary<string, string>
        {
            ["jwks"] = jwks,
            ["issuer"] = "https://issuer.example",
            ["audience"] = "api",
        });

        string Token(object claims, byte[]? signWith = null)
        {
            var head = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\",\"kid\":\"k1\"}"));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signature = HMACSHA256.HashData(signWith ?? key, Encoding.ASCII.GetBytes(head + "." + payload));
            return head + "." + payload + "." + Base64Url(signature);
        }

        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var good = Token(new { sub = "alice", scope = "read write", iss = "https://issuer.example", aud = "api", exp });

        var allowed = await plugin.OnRequestHeadersAsync(
            Request(headers: [H("authorization", "Bearer " + good), H("x-jwt-sub", "mallory")]), default);
        Assert.AreEqual(ScriptAction.Continue, allowed.Action);
        Assert.AreEqual("alice", Header(allowed.SetHeaders, "x-jwt-sub"), "the token's subject replaces a client-sent one");
        Assert.AreEqual("read write", Header(allowed.SetHeaders, "x-jwt-scope"));

        var none = await plugin.OnRequestHeadersAsync(Request(), default);
        Assert.AreEqual(401, none.LocalResponse!.Status);

        var forged = Token(new { sub = "alice", iss = "https://issuer.example", aud = "api", exp }, RandomNumberGenerator.GetBytes(32));
        Assert.AreEqual(401, (await plugin.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer " + forged)]), default))
            .LocalResponse!.Status);

        var wrongAudience = Token(new { sub = "alice", iss = "https://issuer.example", aud = "other", exp });
        Assert.AreEqual(401, (await plugin.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer " + wrongAudience)]), default))
            .LocalResponse!.Status);

        var expired = Token(new { sub = "alice", iss = "https://issuer.example", aud = "api", exp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() });
        Assert.AreEqual(401, (await plugin.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer " + expired)]), default))
            .LocalResponse!.Status);
    }

    [TestMethod]
    public async Task AuthCallout_FollowsTheAuthServicesAnswer()
    {
        RequireNative();
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var credentials = context.Request.Headers["Authorization"];
                switch (credentials)
                {
                    case "Bearer good":
                        context.Response.StatusCode = 200;
                        context.Response.Headers["x-auth-user"] = "alice";
                        break;
                    case "Bearer denied":
                        context.Response.StatusCode = 403;
                        break;
                    default:
                        context.Response.StatusCode = 500;
                        break;
                }

                context.Response.Close();
            }
        });

        try
        {
            using var plugin = CreateFile(Recipe("auth_callout.lua"), "auth-callout", new Dictionary<string, string>
            {
                ["authUrl"] = $"http://127.0.0.1:{port}/check",
            }, calloutHosts: [$"127.0.0.1:{port}"]);

            var none = await plugin.OnRequestHeadersAsync(Request(), default);
            Assert.AreEqual(401, none.LocalResponse!.Status);

            var good = await plugin.OnRequestHeadersAsync(
                Request(headers: [H("authorization", "Bearer good"), H("x-auth-user", "mallory")]), default);
            Assert.AreEqual(ScriptAction.Continue, good.Action);
            Assert.AreEqual("alice", Header(good.SetHeaders, "x-auth-user"));

            var denied = await plugin.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer denied")]), default);
            Assert.AreEqual(403, denied.LocalResponse!.Status);

            var broken = await plugin.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer other")]), default);
            Assert.AreEqual(503, broken.LocalResponse!.Status, "an auth service that fails never opens the route");
        }
        finally
        {
            listener.Stop();
            await serving;
        }

        using var offline = CreateFile(Recipe("auth_callout.lua"), "auth-offline", new Dictionary<string, string>
        {
            ["authUrl"] = $"http://127.0.0.1:{port}/check",
            ["timeoutMs"] = "200",
        }, calloutHosts: [$"127.0.0.1:{port}"]);
        var unreachable = await offline.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer good")]), default);
        Assert.AreEqual(503, unreachable.LocalResponse!.Status);

        using var refused = CreateFile(Recipe("auth_callout.lua"), "auth-refused", new Dictionary<string, string>
        {
            ["authUrl"] = $"http://127.0.0.1:{port}/check",
        });
        var notAllowed = await refused.OnRequestHeadersAsync(Request(headers: [H("authorization", "Bearer good")]), default);
        Assert.AreEqual(503, notAllowed.LocalResponse!.Status, "a callout outside calloutHosts is refused");
    }

    [TestMethod]
    public async Task TcpFilter_RefusesCidrs_AndClosesPastTheByteCap()
    {
        using var plugin = CreateFile(Recipe("tcp_filter.lua"), "tcp-filter", new Dictionary<string, string>
        {
            ["denyCidrs"] = "192.0.2.0/24, 198.51.100.0/24",
            ["maxBytes"] = "10",
        });

        StreamConnectionContext Connection(string ip) => new()
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Parse(ip), 4000),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            Items = new Dictionary<string, object?>(),
        };

        Assert.AreEqual(ScriptAction.Deny, (await plugin.OnNewConnectionAsync(Connection("198.51.100.9"), default)).Action);

        var connection = Connection("10.1.1.1");
        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnNewConnectionAsync(connection, default)).Action);
        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnDownstreamDataAsync(connection, "12345"u8.ToArray(), default)).Action);
        Assert.AreEqual(ScriptAction.Continue, (await plugin.OnUpstreamDataAsync(connection, "12345"u8.ToArray(), default)).Action);
        Assert.AreEqual(ScriptAction.Deny, (await plugin.OnDownstreamDataAsync(connection, "6"u8.ToArray(), default)).Action);
        await plugin.OnCloseAsync(connection);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
