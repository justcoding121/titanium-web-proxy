using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>TcpConnectionFactory</c> suite: unresolved host fails fast without wedging the proxy.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryUnresolvedHostWave41FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnresolvedHost_ReturnsGatewayOrBadRequest()
    {
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        var uri = "http://this-host-does-not-resolve-abcxyz.invalid/";
        try
        {
            using var response = await client.GetAsync(uri);
            Assert.IsTrue(
                response.StatusCode is System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.GatewayTimeout
                    or System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.ServiceUnavailable,
                $"Expected failure status for unresolved host; got {(int)response.StatusCode}");
        }
        catch (HttpRequestException)
        {
            // Client may surface transport failure instead of an HTTP status — still acceptable.
        }
    }
}
