using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Fat LuaCallouts.CallAsync branches without a Lua VM: URL gates, allow-list, timeout, body cap, hop-by-hop.
/// </summary>
[TestClass]
public class LuaCalloutsHttpBehaviorTests
{
    private static LuaOptions Options(params string[] hosts) =>
        Options(1024, 1, hosts);

    private static LuaOptions Options(long bufferedBodyBytes, int concurrency, params string[] hosts)
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lua-callout-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, "-- empty");
        Assert.IsTrue(LuaOptions.TryParse(new PluginConfig
        {
            Name = "callouts",
            File = path,
            CalloutHosts = hosts,
            Limits = new PluginLimitsConfig
            {
                CalloutConcurrency = concurrency,
                BufferedBodyBytes = bufferedBodyBytes,
            },
        }, out var options, out var error), error);
        return options!;
    }

    private static byte[] S(string value) => Encoding.UTF8.GetBytes(value);

    private static LuaTable Op(string url, string? method = "GET", byte[]? body = null, LuaTable? headers = null)
    {
        var op = new LuaTable();
        op.Fields["url"] = S(url);
        if (method is not null)
        {
            op.Fields["method"] = S(method);
        }

        if (body is not null)
        {
            op.Fields["body"] = body;
        }

        if (headers is not null)
        {
            op.Fields["headers"] = headers;
        }

        return op;
    }

    [TestMethod]
    public async Task CallAsync_RejectsBadUrl_Credentials_AndDisallowedHost()
    {
        using var callouts = new LuaCallouts(Options("api.test"), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var badScheme = Decode(await callouts.CallAsync(Op("ftp://api.test/"), default));
        StringAssert.Contains(badScheme.Error!, "absolute http or https");

        var creds = Decode(await callouts.CallAsync(Op("http://user:pass@api.test/"), default));
        StringAssert.Contains(creds.Error!, "without credentials");

        var denied = Decode(await callouts.CallAsync(Op("http://evil.test/"), default));
        StringAssert.Contains(denied.Error!, "not in calloutHosts");
    }

    [TestMethod]
    public async Task CallAsync_ReturnsStatusHeadersBody_AndSkipsHopByHopHeaders()
    {
        HttpRequestMessage? seen = null;
        using var callouts = new LuaCallouts(Options("api.test"), new StubHandler(req =>
        {
            seen = req;
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.TryAddWithoutValidation("x-ok", "1");
            response.Content = new ByteArrayContent("hi"u8.ToArray());
            response.Content.Headers.TryAddWithoutValidation("content-type", "text/plain");
            return response;
        }));

        var headers = new LuaTable();
        headers.Fields["x-custom"] = S("v");
        headers.Fields["connection"] = S("keep-alive");
        headers.Fields["x-bad"] = S("line\r\n");

        var result = Decode(await callouts.CallAsync(Op("http://api.test/path", "POST", "hi"u8.ToArray(), headers), default));
        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(201L, result.Table!.Int("status"));
        CollectionAssert.AreEqual("hi"u8.ToArray(), result.Table.Bytes("body"));
        Assert.IsNotNull(seen);
        Assert.IsTrue(seen!.Headers.Contains("x-custom"));
        Assert.IsFalse(seen.Headers.Contains("connection"));
    }

    [TestMethod]
    public async Task CallAsync_TimesOut_AndRejectsOversizedBody_AndConcurrency()
    {
        using var slow = new LuaCallouts(Options("api.test"), new StubHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var timed = new LuaTable();
        timed.Fields["url"] = S("http://api.test/");
        timed.Fields["timeout_ms"] = 20L;
        var timeout = Decode(await slow.CallAsync(timed, default));
        StringAssert.Contains(timeout.Error!, "timed out");

        using var fat = new LuaCallouts(Options(1024, 1, "api.test"), new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', 2048))),
            };
            return response;
        }));
        var oversized = Decode(await fat.CallAsync(Op("http://api.test/"), default));
        StringAssert.Contains(oversized.Error!, "larger than");

        var hold = new HoldHandler();
        using var limited = new LuaCallouts(Options(1024, 1, "api.test"), hold);
        var first = limited.CallAsync(Op("http://api.test/a"), default);
        await Task.Delay(40);
        var second = Decode(await limited.CallAsync(Op("http://api.test/b"), default));
        StringAssert.Contains(second.Error!, "already in flight");
        hold.Release();
        _ = Decode(await first);
    }

    [TestMethod]
    public void Allows_MatchesWildcardHostAndPortAuthority()
    {
        Assert.IsTrue(LuaCallouts.Allows("*.example.test", new Uri("http://a.example.test/")));
        Assert.IsFalse(LuaCallouts.Allows("*.example.test", new Uri("http://example.test/")));
        Assert.IsTrue(LuaCallouts.Allows("api.test:8443", new Uri("https://api.test:8443/")));
        Assert.IsFalse(LuaCallouts.Allows("api.test:8443", new Uri("https://api.test/")));
    }

    private static (LuaTable? Table, string? Error) Decode(byte[] encoded)
    {
        var table = (LuaTable)new LuaReader(encoded).Read()!;
        return (table[1] as LuaTable, LuaReader.AsString(table[2]));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(req => Task.FromResult(sync(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class HoldHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }
}
