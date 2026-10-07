using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression (2026-10-07): an HTTP/1.1-only origin rejects the h2-only ALPN probe
///     (SEC_E_NO_APPLICATION_PROTOCOL). That is an expected, definitive answer — the client must still get
///     its response over HTTP/1.1 and the proxy must not log it as a warning or error.
/// </summary>
[TestClass]
public class AlpnRejectedByOriginTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(30 * 1000)]
    public async Task AlpnRejectedByOrigin_ClientStillGetsResponse_WithoutWarningsOrErrors()
    {
        using var origin = new Http11OnlyOriginServer(TestCertificateAuthority.ServerCertificate);

        var logs = new LevelCapturingLoggerFactory();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.Logging.LoggerFactory = logs;
        proxy.EnableHttp2 = true;
        proxy.EnableTcpServerConnectionPrefetch = false;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync($"https://localhost:{origin.Port}/");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, logs.Count(LogLevel.Error), logs.Describe(LogLevel.Error));
        Assert.AreEqual(0, logs.Count(LogLevel.Warning), logs.Describe(LogLevel.Warning));
    }
}
