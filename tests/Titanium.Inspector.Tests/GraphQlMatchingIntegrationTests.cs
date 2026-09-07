using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class GraphQlMatchingIntegrationTests
{
    [TestMethod]
    public void MapRemote_FiltersByOperationName()
    {
        var vm = new MapRemoteViewModel { Enabled = true };
        vm.Rules.Add(new MapRemoteRule
        {
            MatchUrl = "*graphql*",
            TargetUrl = "http://127.0.0.1:9/ok",
            GraphQlOperationName = "GetUser",
            Enabled = true,
        });
        Assert.IsFalse(vm.TryRewrite("http://x/graphql", """{"operationName":"Other"}""", out _, out _));
        Assert.IsTrue(vm.TryRewrite("http://x/graphql", """{"operationName":"GetUser"}""", out var rewritten, out _));
        Assert.AreEqual("http://127.0.0.1:9/ok", rewritten);
    }

    [TestMethod]
    public void TryGetOperationName_FromQueryDocument()
    {
        Assert.IsTrue(GraphQlOperationMatcher.TryGetOperationName(
            """{"query":"mutation CreateItem { createItem { id } }"}""",
            out var name));
        Assert.AreEqual("CreateItem", name);
    }

    [TestMethod]
    public async Task Interception_AutoResponder_MatchesGraphQlOperation()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.AutoResponder = new AutoResponderViewModel { Enabled = true };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            MatchUrl = "*graphql*",
            StatusCode = 200,
            Body = "{\"data\":{\"user\":{\"id\":\"1\"}}}",
            ContentType = "application/json",
            GraphQlOperationName = "GetUser",
            Enabled = true,
        });

        await interception.StartAsync(IPAddress.Loopback, 0);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        using var miss = new StringContent("""{"operationName":"Other","query":"query Other { x }"}""", Encoding.UTF8, "application/json");
        // Unresolvable host: missed AutoResponder fails fast rather than hanging DNS.
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _ = await http.PostAsync("http://127.0.0.1:9/graphql", miss, cts.Token);
        });

        using var hit = new StringContent("""{"operationName":"GetUser","query":"query GetUser { user { id } }"}""", Encoding.UTF8, "application/json");
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await http.PostAsync("http://127.0.0.1:9/graphql", hit, cts2.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts2.Token);
        StringAssert.Contains(body, "\"id\":\"1\"");
        interception.Stop();
    }
}
