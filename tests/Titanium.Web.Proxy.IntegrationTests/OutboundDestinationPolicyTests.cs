using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Phase E.15: outbound destination policy hook. Off by default (upstream chaining to
///     <c>localhost</c> must keep working out of the box); when enabled, a request whose resolved
///     destination is loopback/private/link-local must be rejected instead of forwarded, and an
///     explicitly configured upstream proxy address must remain exempt.
/// </summary>
[DoNotParallelize]
[TestClass]
public class OutboundDestinationPolicyTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    public async Task Default_Disabled_AllowsLoopbackDestination()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        Assert.IsFalse(proxy.BlockPrivateNetworkDestinations, "must be off by default");

        var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Enabled_BlocksLoopbackDestination()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.BlockPrivateNetworkDestinations = true;

        var client = testSuite.GetClient(proxy);

        // Plain (non-CONNECT) requests have no synthesized error-response path on a server-connection
        // failure: the proxy tears down the client connection, which surfaces to the HttpClient as a
        // transport-level failure rather than a well-formed 5xx response.
        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(server.ListeningHttpUrl)),
            "a blocked private-network destination must never be forwarded to");
    }

    [TestMethod]
    public async Task Enabled_ExemptsExplicitlyConfiguredUpstreamProxy()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        // The upstream hop itself has no restriction, but is on loopback like everything else in this
        // test harness. `proxy` only ever connects directly to `upstream` (its configured
        // UpStreamHttpProxy), which must be exempt even though that address is also loopback. Distinct
        // Via pseudonyms are required so the two chained hops don't trip RFC 9110 §7.6.3 loop detection
        // against each other (both default to the same pseudonym otherwise).
        var upstream = testSuite.GetProxy();
        upstream.ViaHeaderPseudonym = "upstream-proxy";
        var proxy = testSuite.GetProxy(upstream);
        proxy.ViaHeaderPseudonym = "outer-proxy";
        proxy.BlockPrivateNetworkDestinations = true;

        var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "an explicitly configured upstream proxy address must be exempt from the destination block");
    }
}
