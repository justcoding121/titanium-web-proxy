using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;
using static Titanium.Web.Proxy.IntegrationTests.ProtocolMatrixTests;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     The extAuth policy on a reverse-proxy route: an authorization service is asked before anything is
///     forwarded, from each client protocol that can reach the route.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExtAuthRouteTests
{
    private const string Good = "Bearer good";

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Allow_CopiesIdentityHeaders_OntoTheUpstreamRequest(ClientKind kind)
    {
        await using var service = new AuthService(seen => seen.Header("Authorization") == Good
            ? new AuthAnswer(200, [("X-User", "alice"), ("X-Email", "alice@example.test"), ("X-Ignored", "no")])
            : new AuthAnswer(401));
        string? upstream = null;
        await using var origin = new RecordingOrigin((request, _) =>
        {
            Volatile.Write(ref upstream, request);
            return (200, "origin-ok");
        });
        var policy = ExtAuth(service.Url, copy: ["X-User", "X-Email"]);
        await using var proxy = await StartMatrixAsync(origin.Port, Routes("auth"), kind, [policy]);

        using var client = NewClient(proxy, kind);
        using var response = await client.SendAsync(NewRequest(proxy, kind, "/secret?a=1", ("Authorization", Good)));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(kind == ClientKind.H2Tls ? HttpVersion.Version20 : HttpVersion.Version11, response.Version);
        Assert.AreEqual("origin-ok", await response.Content.ReadAsStringAsync());
        var seenUpstream = Volatile.Read(ref upstream)!;
        Assert.AreEqual("alice", HeaderOf(seenUpstream, "X-User"));
        Assert.AreEqual("alice@example.test", HeaderOf(seenUpstream, "X-Email"));
        Assert.IsNull(HeaderOf(seenUpstream, "X-Ignored"), "only the configured names are copied");
        Assert.IsNull(HeaderOf(seenUpstream, "X-Titanium-ExtAuth"), "the loop marker never reaches the origin");
        Assert.AreEqual(Good, HeaderOf(seenUpstream, "Authorization"));

        var asked = service.Requests.Single();
        Assert.AreEqual("GET", asked.Method);
        Assert.AreEqual(Good, asked.Header("Authorization"));
        Assert.AreEqual("1", asked.Header("X-Titanium-ExtAuth"));
        Assert.AreEqual("GET", asked.Header("X-Original-Method"));
        Assert.AreEqual("/secret?a=1", asked.Header("X-Original-Uri"));
        Assert.AreEqual("127.0.0.1", asked.Header("X-Forwarded-For"));
        Assert.IsFalse(string.IsNullOrEmpty(asked.Header("X-Original-Host")));
        Assert.IsNull(asked.Header("Cookie"), "only the configured SendHeaders are sent");
        if (kind == ClientKind.H1Cleartext)
        {
            Assert.AreEqual("matrix.test", asked.Header("X-Original-Host"));
        }
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Deny_Returns401_WithWwwAuthenticate_AndSkipsTheOrigin(ClientKind kind)
    {
        await using var service = new AuthService(_ => new AuthAnswer(
            401,
            [
                ("WWW-Authenticate", "Bearer realm=\"gate\""),
                ("Content-Type", "text/plain"),
                ("Set-Cookie", "gate=1"),
                ("X-Internal", "secret"),
            ],
            "login required"));
        await using var origin = new RecordingOrigin((_, _) => (200, "must-not-run"));
        await using var proxy = await StartMatrixAsync(origin.Port, Routes("auth"), kind, [ExtAuth(service.Url)]);

        using var client = NewClient(proxy, kind);
        using var response = await client.SendAsync(NewRequest(proxy, kind, "/secret"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(kind == ClientKind.H2Tls ? HttpVersion.Version20 : HttpVersion.Version11, response.Version);
        Assert.AreEqual("Bearer realm=\"gate\"", response.Headers.WwwAuthenticate.ToString());
        Assert.IsTrue(response.Headers.Contains("Set-Cookie"));
        Assert.IsFalse(response.Headers.Contains("X-Internal"), "headers outside DenyResponseHeaders are dropped");
        Assert.AreEqual("login required", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(0, origin.Hits);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Redirect_IsRelayed_NotFollowed(ClientKind kind)
    {
        await using var landing = new AuthService(_ => new AuthAnswer(200));
        var target = landing.Url + "/login";
        await using var service = new AuthService(_ => new AuthAnswer(302, [("Location", target)]));
        await using var origin = new RecordingOrigin((_, _) => (200, "must-not-run"));
        await using var proxy = await StartMatrixAsync(origin.Port, Routes("auth"), kind, [ExtAuth(service.Url)]);

        using var client = NewClient(proxy, kind);
        using var response = await client.SendAsync(NewRequest(proxy, kind, "/private"));

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual(target, response.Headers.Location?.ToString());
        Assert.AreEqual(1, service.Requests.Count);
        Assert.AreEqual(0, landing.Requests.Count, "the proxy must not follow the redirect");
        Assert.AreEqual(0, origin.Hits);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext, "timeout", false)]
    [DataRow(ClientKind.H1Cleartext, "timeout", true)]
    [DataRow(ClientKind.H1Tls, "status500", false)]
    [DataRow(ClientKind.H1Tls, "status500", true)]
    [DataRow(ClientKind.H2Tls, "refused", false)]
    [DataRow(ClientKind.H2Tls, "refused", true)]
    [Timeout(30_000)]
    public async Task ServiceFailure_FailsClosedWith503_OrOpenWhenConfigured(ClientKind kind, string failure, bool failOpen)
    {
        await using var service = new AuthService(_ => failure == "status500"
            ? new AuthAnswer(500)
            : new AuthAnswer(200, Delay: TimeSpan.FromSeconds(5)));
        var url = failure == "refused" ? "http://127.0.0.1:1/auth" : service.Url;
        await using var origin = new RecordingOrigin((_, _) => (200, "origin-ok"));
        var policy = ExtAuth(url, timeoutMs: 300, failOpen: failOpen);
        using var metrics = new ExtAuthMetrics();
        await using var proxy = await StartMatrixAsync(origin.Port, Routes("auth"), kind, [policy]);

        using var client = NewClient(proxy, kind);
        using var response = await client.SendAsync(NewRequest(proxy, kind, "/x"));

        if (failOpen)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, origin.Hits);
            Assert.AreEqual(1, metrics.Count("error_open"));
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(0, origin.Hits);
            Assert.AreEqual(1, metrics.Count("error_closed"));
        }
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task ClientSuppliedIdentityHeader_IsStripped_EvenWhenTheServiceSetsNothing(ClientKind kind)
    {
        var answer = new AuthAnswer(200);
        await using var service = new AuthService(_ => answer);
        string? upstream = null;
        await using var origin = new RecordingOrigin((request, _) =>
        {
            Volatile.Write(ref upstream, request);
            return (200, "ok");
        });
        var policy = ExtAuth(service.Url, copy: ["X-User"]);
        await using var proxy = await StartMatrixAsync(origin.Port, Routes("auth"), kind, [policy]);
        using var client = NewClient(proxy, kind);

        using (var response = await client.SendAsync(NewRequest(proxy, kind, "/a", ("X-User", "mallory"))))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.IsNull(HeaderOf(Volatile.Read(ref upstream)!, "X-User"), "a forged identity must not survive");

        answer = new AuthAnswer(200, [("X-User", "alice")]);
        using (var response = await client.SendAsync(NewRequest(proxy, kind, "/b", ("X-User", "mallory"))))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var seen = Volatile.Read(ref upstream)!;
        Assert.AreEqual("alice", HeaderOf(seen, "X-User"));
        Assert.IsFalse(seen.Contains("mallory", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOf(seen, "X-User"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PositiveCache_AnswersRepeatCredentialsWithoutAskingAgain()
    {
        await using var service = new AuthService(seen => seen.Header("Authorization") == Good
            ? new AuthAnswer(200, [("X-User", "alice")])
            : new AuthAnswer(401));
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        var policy = ExtAuth(service.Url, cacheSeconds: 60, copy: ["X-User"]);
        using var metrics = new ExtAuthMetrics();
        await using var proxy = await StartAsync(origin.Port, Routes("auth"), ClientKind.H1Cleartext, [policy]);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/p", ("Authorization", Good)));
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }

        Assert.AreEqual(1, service.Requests.Count, "two of three were answered from the cache");
        Assert.AreEqual(2, metrics.Count("cache_hit"));
        Assert.AreEqual(1, metrics.Count("allow"));

        using var other = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/p", ("Authorization", "Bearer other")));
        Assert.AreEqual(HttpStatusCode.Unauthorized, other.StatusCode);
        using var again = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/p", ("Authorization", "Bearer other")));
        Assert.AreEqual(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.AreEqual(3, service.Requests.Count, "a denial is never cached");

        using var otherPath = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/q", ("Authorization", Good)));
        Assert.AreEqual(HttpStatusCode.OK, otherPath.StatusCode);
        Assert.AreEqual(4, service.Requests.Count, "the decision is keyed by path as well as credentials");

        using var otherQuery = await client.SendAsync(
            NewRequest(proxy, ClientKind.H1Cleartext, "/q?token=other", ("Authorization", Good)));
        Assert.AreEqual(HttpStatusCode.OK, otherQuery.StatusCode);
        Assert.AreEqual(5, service.Requests.Count, "the decision is keyed by query as well as path");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PositiveCache_ExpiresAndEvictsOldestFirst()
    {
        var clock = new ManualClock();
        var previous = RoutePolicies.Clock;
        RoutePolicies.Clock = clock;
        try
        {
            await using var service = new AuthService(_ => new AuthAnswer(200));
            await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
            var policy = ExtAuth(service.Url, cacheSeconds: 10, cacheMax: 2);
            await using var proxy = await StartAsync(origin.Port, Routes("auth"), ClientKind.H1Cleartext, [policy]);
            using var client = NewClient(proxy, ClientKind.H1Cleartext);

            async Task Send(string token)
            {
                using var response = await client.SendAsync(
                    NewRequest(proxy, ClientKind.H1Cleartext, "/p", ("Authorization", token)));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            await Send("t1");
            await Send("t2");
            await Send("t3");
            Assert.AreEqual(3, service.Requests.Count);
            await Send("t3");
            await Send("t2");
            Assert.AreEqual(3, service.Requests.Count, "the two newest are still cached");
            await Send("t1");
            Assert.AreEqual(4, service.Requests.Count, "the oldest entry was evicted");

            clock.Advance(TimeSpan.FromSeconds(11));
            await Send("t1");
            Assert.AreEqual(5, service.Requests.Count, "an expired entry is asked again");
        }
        finally
        {
            RoutePolicies.Clock = previous;
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task KeepAlive_EachRequestCarriesOnlyItsOwnCredentials()
    {
        await using var service = new AuthService(seen => seen.Header("Authorization") switch
        {
            "Bearer a" => new AuthAnswer(200, [("X-User", "user-a")]),
            "Bearer b" => new AuthAnswer(200, [("X-User", "user-b")]),
            _ => new AuthAnswer(401),
        });
        var users = new List<string?>();
        await using var origin = new RecordingOrigin((request, _) =>
        {
            lock (users)
            {
                users.Add(HeaderOf(request, "X-User"));
            }

            return (200, "ok");
        });
        var policy = ExtAuth(service.Url, copy: ["X-User"]);
        await using var proxy = await StartAsync(origin.Port, Routes("auth"), ClientKind.H1Cleartext, [policy]);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        async Task<HttpStatusCode> Send(string? token, string? cookie = null)
        {
            var request = token is null
                ? NewRequest(proxy, ClientKind.H1Cleartext, "/k")
                : NewRequest(proxy, ClientKind.H1Cleartext, "/k", ("Authorization", token));
            if (cookie is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.OK, await Send("Bearer a", "sid=1"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await Send("Bearer nope"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, await Send(null), "no credential must not inherit the previous one");
        Assert.AreEqual(HttpStatusCode.OK, await Send("Bearer b"));
        Assert.AreEqual(HttpStatusCode.OK, await Send("Bearer a"));

        Assert.AreEqual(5, service.Requests.Count);
        CollectionAssert.AreEqual(
            new string?[] { "Bearer a", "Bearer nope", null, "Bearer b", "Bearer a" },
            service.Requests.Select(r => r.Header("Authorization")).ToArray());
        Assert.AreEqual("sid=1", service.Requests[0].Header("Cookie"));
        Assert.IsNull(service.Requests[1].Header("Cookie"), "the earlier cookie must not leak into a later subrequest");
        Assert.AreEqual(1, service.Connections, "the subrequests share one keep-alive connection");
        CollectionAssert.AreEqual(new string?[] { "user-a", "user-b", "user-a" }, users);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task InboundLoopMarker_IsRefused_WithoutCallingTheService()
    {
        await using var service = new AuthService(_ => new AuthAnswer(200));
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        await using var proxy = await StartAsync(origin.Port, Routes("auth"), ClientKind.H1Cleartext, [ExtAuth(service.Url)]);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        using var response = await client.SendAsync(
            NewRequest(proxy, ClientKind.H1Cleartext, "/loop", ("X-Titanium-ExtAuth", "1")));

        Assert.AreEqual((HttpStatusCode)508, response.StatusCode);
        Assert.AreEqual(0, service.Requests.Count);
        Assert.AreEqual(0, origin.Hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DenyBody_IsCappedAt64KiB()
    {
        await using var service = new AuthService(_ => new AuthAnswer(403, [("Content-Type", "text/plain")], new string('x', 200_000)));
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        await using var proxy = await StartAsync(origin.Port, Routes("auth"), ClientKind.H1Cleartext, [ExtAuth(service.Url)]);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        using var response = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/big"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(64 * 1024, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EarlierPolicy_RefusesFirst_AndTheServiceIsNotAsked()
    {
        await using var service = new AuthService(_ => new AuthAnswer(200));
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        var policies = new[]
        {
            new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant", HeaderValue = "ok" },
            ExtAuth(service.Url),
        };
        await using var proxy = await StartAsync(origin.Port, Routes("need", "auth"), ClientKind.H1Cleartext, policies);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        using var response = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/t"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, service.Requests.Count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Denial_GivesBackTheConnectionSlotAnEarlierPolicyHeld()
    {
        await using var service = new AuthService(_ => new AuthAnswer(401));
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        var policies = new[]
        {
            new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 },
            ExtAuth(service.Url),
        };
        RoutePolicies.ResetForTests();
        await using var proxy = await StartAsync(origin.Port, Routes("slots", "auth"), ClientKind.H1Cleartext, policies);
        using var client = NewClient(proxy, ClientKind.H1Cleartext);

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.SendAsync(NewRequest(proxy, ClientKind.H1Cleartext, "/s"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "request " + i);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH2_Allow_CopiesIdentity_AndDeny_SkipsTheOrigin()
    {
        await using var service = new AuthService(seen => seen.Header("Authorization") == Good
            ? new AuthAnswer(200, [("X-User", "alice")])
            : new AuthAnswer(401, [("WWW-Authenticate", "Bearer")]));
        var hits = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, requestHeaders, _) = await connection.ReadRequestAsync();
                Interlocked.Increment(ref hits);
                var users = string.Join(
                    ",", requestHeaders.Where(h => h.Name == "x-user").Select(h => h.Value));
                var payload = Encoding.UTF8.GetBytes("users=" + users);
                var block = connection.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())],
                    Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, block, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var policy = ExtAuth(service.Url, copy: ["X-User"]);
        await using var proxy = await StartH2ToH2Async(origin.Port, [policy]);
        using var client = NewH2Client();
        var url = $"https://127.0.0.1:{proxy.Tls.Port}/h2";

        using (var allowed = new HttpRequestMessage(HttpMethod.Get, url)
               {
                   Version = HttpVersion.Version20,
                   VersionPolicy = HttpVersionPolicy.RequestVersionExact,
               })
        {
            allowed.Headers.TryAddWithoutValidation("Authorization", Good);
            allowed.Headers.TryAddWithoutValidation("X-User", "mallory");
            using var response = await client.SendAsync(allowed);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpVersion.Version20, response.Version);
            Assert.AreEqual("users=alice", await response.Content.ReadAsStringAsync());
        }

        using (var denied = await client.GetAsync(url))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.AreEqual("Bearer", denied.Headers.WwwAuthenticate.ToString());
        }

        Assert.AreEqual(1, Volatile.Read(ref hits));
        Assert.AreEqual(2, service.Requests.Count);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     The shared matrix proxy. An h2 client reaches an HTTP/1.1 origin only through protocol translation, so
    ///     that row opts in on the TLS listener.
    /// </summary>
    private static async Task<Started> StartMatrixAsync(
        int originPort, IReadOnlyList<RouteConfig> routes, ClientKind kind, IReadOnlyList<RoutePolicyConfig> policies)
    {
        var proxy = await StartAsync(originPort, routes, kind, policies);
        if (kind == ClientKind.H2Tls)
        {
            proxy.Tls.BeforeSslAuthenticate += (_, args) =>
            {
                args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                args.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
        }

        return proxy;
    }

    private static RoutePolicyConfig ExtAuth(
        string url,
        string[]? copy = null,
        string[]? send = null,
        int? timeoutMs = null,
        bool? failOpen = null,
        int? cacheSeconds = null,
        int? cacheMax = null) => new()
    {
        Name = "auth",
        Kind = "extAuth",
        Url = url,
        CopyResponseHeaders = copy,
        SendHeaders = send,
        TimeoutMilliseconds = timeoutMs,
        FailOpen = failOpen,
        CacheSeconds = cacheSeconds,
        CacheMaxEntries = cacheMax,
    };
    private static RouteConfig[] Routes(params string[] policies) =>
    [
        new RouteConfig
        {
            Id = "r",
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Policies = policies,
        },
    ];

    private static HttpClient NewClient(Started proxy, ClientKind kind)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = kind == ClientKind.H1Cleartext,
            Proxy = kind == ClientKind.H1Cleartext ? new WebProxy($"http://127.0.0.1:{proxy.Clear.Port}") : null,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        if (kind == ClientKind.H2Tls)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    private static HttpClient NewH2Client()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            },
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private static HttpRequestMessage NewRequest(Started proxy, ClientKind kind, string path,
        params (string Name, string Value)[] headers)
    {
        var url = kind == ClientKind.H1Cleartext
            ? "http://matrix.test" + path
            : $"https://127.0.0.1:{proxy.Tls.Port}{path}";
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (kind == ClientKind.H2Tls)
        {
            message.Version = HttpVersion.Version20;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        foreach (var (name, value) in headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
    }

    private static string? HeaderOf(string request, string name)
    {
        var needle = "\n" + name + ":";
        var at = request.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + needle.Length;
        var end = request.IndexOf('\n', start);
        return (end < 0 ? request[start..] : request[start..end]).Trim();
    }

    private static int CountOf(string request, string name)
    {
        var count = 0;
        var needle = "\n" + name + ":";
        var from = 0;
        while ((from = request.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            from += needle.Length;
        }

        return count;
    }

    private static async Task<H2Started> StartH2ToH2Async(int originPort, IReadOnlyList<RoutePolicyConfig> policies)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ]);

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = Routes("auth"),
            Policies = policies.ToArray(),
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
        };
        var clear = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(clear);
        proxy.AddEndPoint(tls);
        proxy.Start();
        return new H2Started(proxy, tls);
    }

    private sealed class H2Started(ProxyServer server, TransparentProxyEndPoint tls) : IAsyncDisposable
    {
        public TransparentProxyEndPoint Tls { get; } = tls;

        public async ValueTask DisposeAsync()
        {
            if (server.ProxyRunning)
            {
                await server.StopAsync();
            }

            server.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class ExtAuthMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);

        public ExtAuthMetrics()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "twp.extauth.requests")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "result" && tag.Value is string result)
                    {
                        lock (counts)
                        {
                            counts[result] = counts.GetValueOrDefault(result) + value;
                        }
                    }
                }
            });
            listener.Start();
        }

        public long Count(string result)
        {
            lock (counts)
            {
                return counts.GetValueOrDefault(result);
            }
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed record AuthRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, int Connection)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private sealed record AuthAnswer(
        int Status,
        (string Name, string Value)[]? Headers = null,
        string? Body = null,
        TimeSpan Delay = default);

    /// <summary>A keep-alive HTTP/1.1 authorization service that records every subrequest it is sent.</summary>
    private sealed class AuthService : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private readonly Func<AuthRequest, AuthAnswer> answer;
        private readonly List<AuthRequest> requests = new();
        private readonly Task loop;
        private int connections;

        public AuthService(Func<AuthRequest, AuthAnswer> answer)
        {
            this.answer = answer;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = AcceptAsync();
        }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}/auth";

        public int Connections => Volatile.Read(ref connections);

        public IReadOnlyList<AuthRequest> Requests
        {
            get
            {
                lock (requests)
                {
                    return requests.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                _ = ServeAsync(client, Interlocked.Increment(ref connections));
            }
        }

        private async Task ServeAsync(TcpClient client, int connection)
        {
            try
            {
                await using var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                while (!cts.IsCancellationRequested)
                {
                    int end;
                    while ((end = IndexOfHeaderEnd(buffer)) < 0)
                    {
                        var n = await stream.ReadAsync(chunk, cts.Token);
                        if (n == 0)
                        {
                            return;
                        }

                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }

                    var head = Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
                    buffer.RemoveRange(0, end + 4);
                    var request = Parse(head, connection);
                    lock (requests)
                    {
                        requests.Add(request);
                    }

                    var reply = answer(request);
                    if (reply.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(reply.Delay, cts.Token);
                    }

                    var body = Encoding.ASCII.GetBytes(reply.Body ?? string.Empty);
                    var text = new StringBuilder("HTTP/1.1 ").Append(reply.Status).Append(" X\r\n");
                    text.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                    foreach (var (name, value) in reply.Headers ?? [])
                    {
                        text.Append(name).Append(": ").Append(value).Append("\r\n");
                    }

                    text.Append("\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), cts.Token);
                    await stream.WriteAsync(body, cts.Token);
                }
            }
            catch
            {
                // The proxy closed the socket, or the test finished.
            }
            finally
            {
                client.Dispose();
            }
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static AuthRequest Parse(string head, int connection)
        {
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            }

            return new AuthRequest(first[0], first.Length > 1 ? first[1] : string.Empty, headers, connection);
        }
    }
}
