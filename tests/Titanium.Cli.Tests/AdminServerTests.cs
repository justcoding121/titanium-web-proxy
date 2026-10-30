using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;

namespace Titanium.Cli.Tests;

/// <summary>The admin listener answers probes unauthenticated and protects <c>/metrics</c> and <c>/version</c> with a bearer token.</summary>
[TestClass]
public class AdminServerTests
{
    private const string Token = "s3cret-admin-token";

    private static readonly AdminVersionInfo Version = new("7.0.27", "abc1234", ".NET 10.0.0", "community", "deadbeef");

    [TestMethod]
    public async Task Healthz_IsAlwaysOk_EvenWhenNotReadyAndTokenIsSet()
    {
        using var fixture = new Fixture(token: Token);

        var response = await fixture.GetAsync("/healthz");

        Assert.AreEqual(200, response.Status);
        Assert.AreEqual("ok\n", response.Body);
    }

    [TestMethod]
    public async Task Readyz_Is503WithReasonsWhileStarting_Then200_Then503WhenDraining()
    {
        using var fixture = new Fixture(token: Token);

        var starting = await fixture.GetAsync("/readyz");
        Assert.AreEqual(503, starting.Status);
        using (var doc = JsonDocument.Parse(starting.Body))
        {
            Assert.AreEqual("not_ready", doc.RootElement.GetProperty("status").GetString());
            var reasons = doc.RootElement.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToArray();
            CollectionAssert.AreEqual(
                new[] { ReadinessState.ReasonConfigNotLoaded, ReadinessState.ReasonListenersNotBound },
                reasons);
        }

        fixture.Readiness.MarkConfigLoaded();
        fixture.Readiness.MarkListenersBound();
        var ready = await fixture.GetAsync("/readyz");
        Assert.AreEqual(200, ready.Status);
        using (var doc = JsonDocument.Parse(ready.Body))
        {
            Assert.AreEqual("ready", doc.RootElement.GetProperty("status").GetString());
        }

        fixture.Readiness.BeginDrain();
        var draining = await fixture.GetAsync("/readyz");
        Assert.AreEqual(503, draining.Status);
        StringAssert.Contains(draining.Body, ReadinessState.ReasonDraining);
    }

    [TestMethod]
    public async Task Probes_NeverNeedTheToken()
    {
        using var fixture = new Fixture(token: Token);
        fixture.Readiness.MarkConfigLoaded();
        fixture.Readiness.MarkListenersBound();

        Assert.AreEqual(200, (await fixture.GetAsync("/healthz")).Status);
        Assert.AreEqual(200, (await fixture.GetAsync("/readyz")).Status);
    }

    [TestMethod]
    public async Task MetricsAndVersion_RequireTheBearerToken()
    {
        using var fixture = new Fixture(token: Token);

        foreach (var path in new[] { "/metrics", "/version" })
        {
            var anonymous = await fixture.GetAsync(path);
            Assert.AreEqual(401, anonymous.Status, path);
            StringAssert.Contains(anonymous.Head, "WWW-Authenticate: Bearer");
            Assert.IsFalse(anonymous.Body.Contains("twp_", StringComparison.Ordinal));

            Assert.AreEqual(401, (await fixture.GetAsync(path, "Bearer wrong")).Status, path);
            Assert.AreEqual(401, (await fixture.GetAsync(path, Token)).Status, path + " without the Bearer scheme");
            Assert.AreEqual(401, (await fixture.GetAsync(path, "Bearer " + Token + "x")).Status, path);
            Assert.AreEqual(401, (await fixture.GetAsync(path, "Bearer ")).Status, path);

            Assert.AreEqual(200, (await fixture.GetAsync(path, "Bearer " + Token)).Status, path);
            Assert.AreEqual(200, (await fixture.GetAsync(path, "bearer " + Token)).Status, path + " scheme is case-insensitive");
        }
    }

    [TestMethod]
    public async Task MetricsAndVersion_AreOpenWhenNoTokenIsConfigured()
    {
        using var fixture = new Fixture(token: null);

        Assert.IsFalse(fixture.Server.RequiresToken);
        Assert.AreEqual(200, (await fixture.GetAsync("/metrics")).Status);
        Assert.AreEqual(200, (await fixture.GetAsync("/version")).Status);
    }

    [TestMethod]
    public async Task Metrics_ReusesTheExpositionAndItsContentType()
    {
        using var fixture = new Fixture(token: Token);
        fixture.Exposition.AddAppender(sb => sb.Append("# admin_appender 1\n"));

        var response = await fixture.GetAsync("/metrics", "Bearer " + Token);

        Assert.AreEqual(200, response.Status);
        StringAssert.Contains(response.Head, "Content-Type: text/plain; version=0.0.4");
        StringAssert.Contains(response.Body, "# admin_appender 1");
    }

    [TestMethod]
    public async Task Version_ReportsBuildRuntimeEditionAndConfigHash()
    {
        using var fixture = new Fixture(token: Token);

        var response = await fixture.GetAsync("/version", "Bearer " + Token);

        Assert.AreEqual(200, response.Status);
        StringAssert.Contains(response.Head, "Content-Type: application/json");
        using var doc = JsonDocument.Parse(response.Body);
        Assert.AreEqual("7.0.27", doc.RootElement.GetProperty("version").GetString());
        Assert.AreEqual("abc1234", doc.RootElement.GetProperty("commit").GetString());
        Assert.AreEqual(".NET 10.0.0", doc.RootElement.GetProperty("runtime").GetString());
        Assert.AreEqual("community", doc.RootElement.GetProperty("edition").GetString());
        Assert.AreEqual("deadbeef", doc.RootElement.GetProperty("config_hash").GetString());
    }

    [TestMethod]
    public async Task SetAuthToken_RotatesWithoutARestart()
    {
        using var fixture = new Fixture(token: Token);

        fixture.Server.SetAuthToken("rotated");

        Assert.AreEqual(401, (await fixture.GetAsync("/version", "Bearer " + Token)).Status);
        Assert.AreEqual(200, (await fixture.GetAsync("/version", "Bearer rotated")).Status);

        fixture.Server.SetAuthToken(null);
        Assert.AreEqual(200, (await fixture.GetAsync("/version")).Status);
    }

    [TestMethod]
    public async Task UnknownPathMethodAndHead_AreHandled()
    {
        using var fixture = new Fixture(token: Token);

        Assert.AreEqual(404, (await fixture.GetAsync("/nope")).Status);
        Assert.AreEqual(405, (await fixture.SendAsync("POST /healthz HTTP/1.1")).Status);

        var head = await fixture.SendAsync("HEAD /healthz HTTP/1.1");
        Assert.AreEqual(200, head.Status);
        Assert.AreEqual(string.Empty, head.Body);
        StringAssert.Contains(head.Head, "Content-Length: 3");
    }

    [TestMethod]
    public async Task QueryString_IsIgnoredForRouting()
    {
        using var fixture = new Fixture(token: Token);

        Assert.AreEqual(200, (await fixture.GetAsync("/healthz?probe=1")).Status);
    }

    [TestMethod]
    public void TokenMatches_IsExactAndRejectsNull()
    {
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Token));

        Assert.IsTrue(AdminServer.TokenMatches(digest, Token));
        Assert.IsFalse(AdminServer.TokenMatches(digest, Token.ToUpperInvariant()));
        Assert.IsFalse(AdminServer.TokenMatches(digest, string.Empty));
        Assert.IsFalse(AdminServer.TokenMatches(digest, null));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string? token)
        {
            Readiness = new ReadinessState();
            Exposition = new MetricsExposition();
            Server = new AdminServer(IPAddress.Loopback, 0, Readiness, Exposition, () => Version, token);
            Server.Start();
        }

        public ReadinessState Readiness { get; }

        public MetricsExposition Exposition { get; }

        public AdminServer Server { get; }

        public Task<Reply> GetAsync(string path, string? authorization = null) =>
            SendAsync($"GET {path} HTTP/1.1", authorization);

        public async Task<Reply> SendAsync(string requestLine, string? authorization = null)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Server.Port);
            var stream = client.GetStream();
            var auth = authorization is null ? string.Empty : $"Authorization: {authorization}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine}\r\nHost: localhost\r\n{auth}\r\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = split < 0 ? text : text[..split];
            var body = split < 0 ? string.Empty : text[(split + 4)..];
            var status = int.Parse(head.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            return new Reply(status, head, body);
        }

        public void Dispose()
        {
            Server.Dispose();
            Exposition.Dispose();
        }
    }

    private sealed record Reply(int Status, string Head, string Body);
}
