using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Phase F.16: exercises the <see cref="PolicyMode" />/<see cref="ProxyPolicyModes" /> observe/enforce
///     switch and the <see cref="ProxyProfile" /> bundle end to end, on top of the same admission gate and
///     body-budget enforcement points already covered (under their default <see cref="PolicyMode.Enforce" />
///     behavior) by <see cref="ConnectionPoolTests" /> and <see cref="BodyBudgetEnforcementTests" />.
/// </summary>
[DoNotParallelize]
[TestClass]
public class PolicyModeTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    /// <summary>
    ///     Under <see cref="PolicyMode.Observe" />, a connection beyond <c>MaxConcurrentClientConnections</c>
    ///     must still be admitted (unlike the default <see cref="PolicyMode.Enforce" />, which
    ///     <see cref="ConnectionPoolTests.Global_Admission_Gate_Rejects_Beyond_Limit_And_Frees_Capacity_Promptly_On_Release" />
    ///     already covers).
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task AdmissionControl_Observe_AdmitsConnectionsBeyondTheConfiguredLimit()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.MaxConcurrentClientConnections = 1;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce.With(PolicyFamily.AdmissionControl, PolicyMode.Observe);

        using var firstClient = testSuite.GetClient(proxy);
        using var secondClient = testSuite.GetClient(proxy);

        var firstResponse = await firstClient.GetAsync(server.ListeningHttpUrl);
        var secondResponse = await secondClient.GetAsync(server.ListeningHttpUrl);

        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode,
            "Observe mode must record the breach but still admit the connection");
    }

    /// <summary>
    ///     Under <see cref="PolicyMode.Disabled" />, <c>MaxConcurrentClientConnections</c> is not
    ///     consulted at all - not even to record a breach.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task AdmissionControl_Disabled_NeverConsultsTheLimit()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.MaxConcurrentClientConnections = 1;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce.With(PolicyFamily.AdmissionControl, PolicyMode.Disabled);

        using var firstClient = testSuite.GetClient(proxy);
        using var secondClient = testSuite.GetClient(proxy);
        using var thirdClient = testSuite.GetClient(proxy);

        var responses = await Task.WhenAll(
            firstClient.GetAsync(server.ListeningHttpUrl),
            secondClient.GetAsync(server.ListeningHttpUrl),
            thirdClient.GetAsync(server.ListeningHttpUrl));

        foreach (var response in responses)
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     Under <see cref="PolicyMode.Observe" />, an oversized whole-body read must still succeed and
    ///     be forwarded - unlike the default <see cref="PolicyMode.Enforce" />, which
    ///     <see cref="BodyBudgetEnforcementTests.RequestBody_ExceedingMaxBufferedBodyBytes_Returns413_BeforeReachingOrigin" />
    ///     already covers with a 413.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task BodyBudget_Observe_ForwardsAnOversizedRequestBody_InsteadOfRejectingIt()
    {
        using var testSuite = new TestSuite(sharedServer);

        var originReceivedRequest = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            originReceivedRequest = true;
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 1024;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce.With(PolicyFamily.BodyBudget, PolicyMode.Observe);
        proxy.BeforeRequest += async (_, e) => { await e.GetRequestBody(); };

        var client = testSuite.GetClient(proxy);

        var content = new StringContent(new string('a', 4096));
        using var response = await client.PostAsync(server.ListeningHttpUrl, content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "Observe mode must record the breach but still forward the oversized body");
        Assert.IsTrue(originReceivedRequest);
    }

    /// <summary>
    ///     Selecting <see cref="ProxyProfile.PublicFacing" /> must actually enable outbound
    ///     private-network blocking end to end, exactly as if <see cref="ProxyServer.BlockPrivateNetworkDestinations" />
    ///     had been set directly - see <see cref="OutboundDestinationPolicyTests.Enabled_BlocksLoopbackDestination" />.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Profile_PublicFacing_BlocksPrivateNetworkDestinations_EndToEnd()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.Profile = ProxyProfile.PublicFacing;

        var client = testSuite.GetClient(proxy);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => client.GetStringAsync(new Uri(server.ListeningHttpUrl)),
            "PublicFacing must block a private-network (loopback) destination without any extra opt-in");
    }

    /// <summary>
    ///     Selecting <see cref="ProxyProfile.LegacyCompatible" /> drops the admission-control family to
    ///     <see cref="PolicyMode.Observe" />, so a connection beyond <c>MaxConcurrentClientConnections</c>
    ///     must still be admitted end to end, purely as a consequence of the profile selection.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Profile_LegacyCompatible_ObservesAdmissionControl_EndToEnd()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.Profile = ProxyProfile.LegacyCompatible;
        proxy.MaxConcurrentClientConnections = 1;

        using var firstClient = testSuite.GetClient(proxy);
        using var secondClient = testSuite.GetClient(proxy);

        var firstResponse = await firstClient.GetAsync(server.ListeningHttpUrl);
        var secondResponse = await secondClient.GetAsync(server.ListeningHttpUrl);

        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode,
            "LegacyCompatible observes rather than enforces admission control");
    }
}
