using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class Http1FramingSafetyTests
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
    [Timeout(15_000)]
    public async Task CompatibilityMode100Continue_Resolves_DeadlockWithStrictClient()
    {
        // With CompatibilityMode100Continue=true, a strict client gets a synthetic 100
        // immediately, allowing the body to be sent without waiting for origin.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer
        {
            ExpectationResponse = HttpStatusCode.Continue,
            ResponseBody = "server got body"
        };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = false; // default, explicit for clarity
        proxy.CompatibilityMode100Continue = true; // resolve deadlock
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "hello");

        // With compatibility mode, the strict client gets a 100 and can send its body.
        Assert.IsNotNull(response, "Compatibility mode must resolve the strict-client deadlock.");
        Assert.AreEqual((int)HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Default_Mode_StrictClient_Still_Times_Out()
    {
        // Verify that with CompatibilityMode disabled the deadlock baseline still holds.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer
        {
            ExpectationResponse = HttpStatusCode.Continue,
            ResponseBody = "body"
        };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = false;
        proxy.CompatibilityMode100Continue = false; // default
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "hello");

        Assert.IsNull(response, "Default mode with strict client must still time out (deadlock baseline).");
    }

    [TestMethod]
    public void NormalizeMessageFraming_Strips_ContentLength_When_TransferEncoding_Present()
    {
        // Existing behaviour: CL+TE → CL is stripped (RFC 9112 §6.3).
        var headers = new HeaderCollection();
        headers.AddHeader("Transfer-Encoding", "chunked");
        headers.AddHeader("Content-Length", "100");
        headers.NormalizeMessageFraming();
        Assert.IsFalse(headers.HeaderExists("Content-Length"),
            "Content-Length must be stripped when Transfer-Encoding is present (RFC 9112 §6.3).");
        Assert.IsTrue(headers.HeaderExists("Transfer-Encoding"),
            "Transfer-Encoding must be preserved.");
    }

    [TestMethod]
    public void NormalizeMessageFraming_TE_Only_Preserved()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Transfer-Encoding", "chunked");
        headers.NormalizeMessageFraming();
        Assert.IsTrue(headers.HeaderExists("Transfer-Encoding"));
        Assert.IsFalse(headers.HeaderExists("Content-Length"));
    }

    [TestMethod]
    public void NormalizeMessageFraming_ChunkedInNonFinalPosition_IsNormalized()
    {
        // "chunked, gzip" is invalid — chunked must be last; proxy normalizes to just "chunked".
        var headers = new HeaderCollection();
        headers.AddHeader("Transfer-Encoding", "chunked, gzip");
        headers.NormalizeMessageFraming();
        Assert.AreEqual("chunked", headers.GetHeaderValueOrNull("Transfer-Encoding"),
            "Non-final chunked coding must be normalized to just 'chunked'.");
    }

    [TestMethod]
    public void NormalizeMessageFraming_ValidChain_GzipThenChunked_IsUnchanged()
    {
        // "gzip, chunked" is valid — chunked is in final position; no rewrite needed.
        var headers = new HeaderCollection();
        headers.AddHeader("Transfer-Encoding", "gzip, chunked");
        headers.NormalizeMessageFraming();
        // The value should not be collapsed since chunked is already last.
        var te = headers.GetHeaderValueOrNull("Transfer-Encoding");
        Assert.IsNotNull(te);
        Assert.IsTrue(te!.Contains("chunked"),
            "chunked in final position should be preserved.");
    }
}
