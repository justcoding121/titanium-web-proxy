using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/2 synthetic-response API parity: <c>Ok</c>/<c>GenericResponse</c>/
///     <c>Redirect</c>/buffered <c>Respond</c> (previously silently ignored over h2 - the request was still
///     forwarded upstream and the synthetic response discarded, because the dispatch condition only checked
///     for a streamed body) and a <c>BeforeResponse</c>-time <c>Respond</c> replacement (previously sent the
///     stale, pre-handler response object instead of the replacement). See
///     <see cref="Http2Helper.EmitSyntheticResponseAsync" />/<c>ProcessCompleteHeaderBlockAsync</c>.
/// </summary>
[TestClass]
public class Http2SyntheticResponseTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Ok_From_BeforeRequest_Answers_Client_And_Origin_Never_Sees_Request()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-reached");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-body");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("synthetic-ok-body", body);
        Assert.IsFalse(originContacted, "The request was forwarded upstream despite being answered by Ok().");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_GenericResponse_From_BeforeRequest_Answers_Client_With_Given_Status()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-reached");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("teapot-body", (HttpStatusCode)418);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        Assert.AreEqual("teapot-body", body);
        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Redirect_From_BeforeRequest_Answers_Client_With_Location_Header()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-reached");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Redirect("https://example.invalid/redirected");
            return Task.CompletedTask;
        };

        var handler = new SocketsHttpHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            AllowAutoRedirect = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };
        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("https://example.invalid/redirected", response.Headers.Location?.ToString());
        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Buffered_Respond_From_BeforeRequest_Answers_Client_Without_Body()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-reached");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, e) =>
        {
            var noBodyResponse = new Titanium.Web.Proxy.Http.Response
            {
                HttpVersion = e.HttpClient.Request.HttpVersion,
                StatusCode = (int)HttpStatusCode.NoContent
            };
            noBodyResponse.Headers.AddHeader("x-synthetic", "no-body");
            e.Respond(noBodyResponse);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual(string.Empty, body);
        Assert.IsTrue(response.Headers.TryGetValues("x-synthetic", out var values));
        Assert.AreEqual("no-body", System.Linq.Enumerable.Single(values));
        Assert.IsFalse(originContacted);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_BeforeResponse_Respond_Replaces_Already_Received_Response()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("real-origin-body");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeResponse += (_, e) =>
        {
            // Called after the real response's headers have already arrived from the origin - this is
            // exactly the stale-`rr` scenario: HttpClient.Response is swapped out for a new object here.
            var replacement = new Titanium.Web.Proxy.Http.Response
            {
                HttpVersion = e.HttpClient.Request.HttpVersion,
                StatusCode = (int)HttpStatusCode.OK
            };
            replacement.Body = replacement.Encoding.GetBytes("replaced-body");
            e.Respond(replacement);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("replaced-body", body,
            "The client received the stale, pre-handler response body instead of the BeforeResponse replacement.");
    }
}
