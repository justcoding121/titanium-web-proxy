using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Phase 2 (HTTP/2 gap closure) integration tests. Complements the existing HTTP/2 coverage in
///     <see cref="StreamingBodyTests" /> (body-write hooks, RespondStreaming) with tests for the HPACK
///     encoder persistence fix: before Phase 2, <c>Http2Helper.SendHeader</c> constructed a brand-new
///     <c>Encoder</c> (with an empty dynamic table) on every call, so repeated headers across streams/requests
///     on the same HTTP/2 connection were never indexed - see the (now updated) characterization tests in
///     <c>Http2HpackEncoderTests</c>. The encoder is now persisted per connection direction, matching how the
///     decoder was already handled, so these tests exercise many requests over one HTTP/2 connection to prove
///     the dynamic table is actually being reused end-to-end without corrupting headers.
/// </summary>
[TestClass]
public class Http2Tests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests()
    {
        // A long, distinctive value so it would dominate a naive per-call HPACK encoding if it were re-sent
        // literally on every response; a persistent encoder should index it after the first response and
        // reference it on every subsequent one, on the same underlying HTTP/2 connection.
        const string repeatedValue =
            "a-fairly-long-repeated-header-value-used-to-exercise-http2-hpack-dynamic-table-reuse-across-requests";

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.Headers["X-Custom-Repeated"] = repeatedValue;
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(new Version(2, 0), response.Version);
            Assert.AreEqual("ok", body);
            Assert.IsTrue(response.Headers.TryGetValues("X-Custom-Repeated", out var values),
                $"Request #{i} is missing the repeated header.");
            Assert.AreEqual(repeatedValue, values.Single(),
                $"Request #{i}'s repeated header value was corrupted - possible HPACK dynamic-table indexing bug.");
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Repeated_Request_Header_Round_Trips_Correctly_Across_Multiple_Requests()
    {
        // Same as above but for the client -> proxy -> server direction (the encoder for that direction is
        // used only within a single relay task, unlike the client-bound one which is shared across both relay
        // tasks for synthetic responses - so this exercises the simpler, but still previously-unindexed, path).
        const string repeatedValue =
            "another-fairly-long-repeated-header-value-for-the-request-direction-hpack-dynamic-table";

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var receivedValues = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            receivedValues.Add(context.Request.Headers["X-Custom-Repeated"].ToString());
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
            request.Headers.Add("X-Custom-Repeated", repeatedValue);

            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.AreEqual(10, receivedValues.Count);
        Assert.IsTrue(receivedValues.All(v => v == repeatedValue),
            "Every request's repeated header value should have round-tripped intact - possible HPACK dynamic-table indexing bug.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Many_Concurrent_Streams_With_Distinct_Headers_Do_Not_Cross_Contaminate()
    {
        // Fires many concurrent requests over the same HTTP/2 connection (true multiplexing, interleaved
        // frames) each with a stream-specific header value, guarding against the shared encoder/decoder
        // introducing cross-stream contamination now that state is persisted per connection direction.
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            var echo = context.Request.Headers["X-Stream-Id"].ToString();
            context.Response.Headers["X-Stream-Id-Echo"] = echo;
            return context.Response.WriteAsync(echo);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
            request.Headers.Add("X-Stream-Id", i.ToString());

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(i.ToString(), body, $"Stream #{i}'s response body was cross-contaminated.");
            Assert.AreEqual(i.ToString(), response.Headers.GetValues("X-Stream-Id-Echo").Single(),
                $"Stream #{i}'s response header was cross-contaminated.");
        });

        await Task.WhenAll(tasks);
    }
}
