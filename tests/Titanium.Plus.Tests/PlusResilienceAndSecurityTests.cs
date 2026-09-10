using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Resilience;
using Titanium.Plus.Security;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class PlusResilienceAndSecurityTests
{
    [TestMethod]
    public void IdempotentRetryPolicy_OnlySafeMethods()
    {
        Assert.IsTrue(IdempotentRetryPolicy.IsIdempotent("GET"));
        Assert.IsTrue(IdempotentRetryPolicy.IsIdempotent("HEAD"));
        Assert.IsTrue(IdempotentRetryPolicy.IsIdempotent("OPTIONS"));
        Assert.IsTrue(IdempotentRetryPolicy.IsIdempotent("TRACE"));
        Assert.IsFalse(IdempotentRetryPolicy.IsIdempotent("POST"));
        Assert.IsFalse(IdempotentRetryPolicy.IsIdempotent("PUT"));
        Assert.IsFalse(IdempotentRetryPolicy.IsIdempotent(null));
        Assert.IsTrue(IdempotentRetryPolicy.ShouldRetry("GET", 0, 2));
        Assert.IsFalse(IdempotentRetryPolicy.ShouldRetry("GET", 2, 2));
        Assert.IsFalse(IdempotentRetryPolicy.ShouldRetry("POST", 0, 2));
        Assert.IsFalse(IdempotentRetryPolicy.ShouldRetry("GET", 0, 0));
    }

    [TestMethod]
    public void IdempotentRetryGuard_Unset_ReturnsNull()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var ctx = new PlusActivationContext
        {
            ProxyServer = proxy,
            Options = new Dictionary<string, string>(),
        };
        Assert.IsNull(IdempotentRetryGuard.TryStart(ctx, ctx.Options!));
        Assert.IsNull(IdempotentRetryGuard.TryStart(ctx, new Dictionary<string, string>
        {
            ["resilience.retry.idempotentAttempts"] = "0",
        }));
    }

    [TestMethod]
    public void IdempotentRetryGuard_ClampsAttempts()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var options = new Dictionary<string, string>
        {
            ["resilience.retry.idempotentAttempts"] = "99",
        };
        var marker = IdempotentRetryGuard.TryStart(
            new PlusActivationContext { ProxyServer = proxy, Options = options },
            options);
        Assert.IsNotNull(marker);
        Assert.AreEqual(5, marker!.Attempts);
    }

    [TestMethod]
    public void CircuitBreaker_Unset_ReturnsNull()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var ctx = new PlusActivationContext
        {
            ProxyServer = proxy,
            ClusterManager = new ClusterManager(),
            Options = new Dictionary<string, string>(),
        };
        Assert.IsNull(CircuitBreakerController.TryStart(ctx, ctx.Options!));
        Assert.IsNull(CircuitBreakerController.TryStart(ctx, new Dictionary<string, string>
        {
            ["resilience.circuit.enabled"] = "false",
        }));
    }

    [TestMethod]
    public void AccessSecurity_Unset_ReturnsNull()
    {
        Assert.IsNull(AccessSecurity.TryStart(
            new PlusActivationContext
            {
                ProxyServer = new object(),
                Middleware = new List<IProxyMiddleware>(),
            },
            new Dictionary<string, string>()));
    }

    [TestMethod]
    public void AccessSecurity_RegistersApiKeyAndCors()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var middleware = new List<IProxyMiddleware>();
        var options = new Dictionary<string, string>
        {
            ["security.apiKeys"] = "k1,k2",
            ["security.basicUsers"] = "bob:secret",
            ["cors.enabled"] = "true",
            ["cors.allowOrigin"] = "https://app.example",
        };
        var security = AccessSecurity.TryStart(
            new PlusActivationContext
            {
                ProxyServer = proxy,
                Middleware = middleware,
                Options = options,
            },
            options);
        Assert.IsNotNull(security);
        Assert.AreEqual(2, middleware.Count);
        Assert.IsInstanceOfType<ApiKeyBasicAuthMiddleware>(middleware[0]);
        Assert.IsInstanceOfType<CorsMiddleware>(middleware[1]);
    }

    [TestMethod]
    public void ApiKeyBasicAuth_AcceptsKeyOrBasic()
    {
        var mw = new ApiKeyBasicAuthMiddleware(["secret-key"], new Dictionary<string, string> { ["alice"] = "wonder" });
        var ctxKey = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView
            {
                Method = "GET",
                Path = "/",
                Host = "x",
                GetHeaderValues = name =>
                    string.Equals(name, "X-Api-Key", StringComparison.OrdinalIgnoreCase)
                        ? ["secret-key"]
                        : null,
            },
        };
        Assert.IsTrue(mw.IsAuthorized(ctxKey));

        var ctxBasic = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView
            {
                Method = "GET",
                Path = "/",
                Host = "x",
                Authorization = "Basic " + Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes("alice:wonder")),
            },
        };
        Assert.IsTrue(mw.IsAuthorized(ctxBasic));

        var denied = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "GET", Path = "/", Host = "x" },
        };
        Assert.IsFalse(mw.IsAuthorized(denied));

        Assert.IsTrue(ApiKeyBasicAuthMiddleware.TryParseBasic(
            "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:wonder")),
            out var u,
            out var p));
        Assert.AreEqual("alice", u);
        Assert.AreEqual("wonder", p);
    }

    [TestMethod]
    public async Task ApiKeyBasicAuth_DeniesUnauthorized()
    {
        var mw = new ApiKeyBasicAuthMiddleware(["secret-key"], null);
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "GET", Path = "/", Host = "x" },
        };
        var nextCalled = false;
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            nextCalled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        Assert.IsFalse(nextCalled);
        Assert.IsTrue(ctx.IsHandled);
        Assert.AreEqual(401, ctx.HandledStatusCode);
    }

    [TestMethod]
    public void CorsMiddleware_OptionsIsHandled()
    {
        var cors = new CorsMiddleware(allowOrigin: "https://app.example", allowCredentials: true);
        var ctx = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView { Method = "OPTIONS", Path = "/", Host = "x" },
        };
        cors.InvokeAsync(ctx, (_, _) => ValueTask.CompletedTask, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.IsTrue(ctx.IsHandled);
        Assert.AreEqual(204, ctx.HandledStatusCode);
        Assert.IsNotNull(ctx.HandledHeaders);
        Assert.IsTrue(ctx.HandledHeaders!.Exists(h =>
            h.Key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) &&
            h.Value == "https://app.example"));
        Assert.IsTrue(ctx.HandledHeaders.Exists(h =>
            h.Key.Equals("Access-Control-Allow-Credentials", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CorsMiddleware_BuildResponseHeaders()
    {
        var cors = new CorsMiddleware("https://a.example", "GET,POST", "X-Custom", allowCredentials: true, maxAgeSeconds: 60);
        var headers = cors.BuildResponseHeaders();
        Assert.IsTrue(headers.Any(h => h.Name == "Access-Control-Allow-Origin" && h.Value == "https://a.example"));
        Assert.IsTrue(headers.Any(h => h.Name == "Access-Control-Allow-Methods" && h.Value == "GET,POST"));
        Assert.IsTrue(headers.Any(h => h.Name == "Access-Control-Max-Age" && h.Value == "60"));
        Assert.IsTrue(headers.Any(h => h.Name == "Access-Control-Allow-Credentials" && h.Value == "true"));
    }

    [TestMethod]
    public async Task CircuitBreaker_EjectsAfterThreshold()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 9 }],
            },
        ]);

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var options = new Dictionary<string, string>
        {
            ["resilience.circuit.enabled"] = "true",
            ["resilience.circuit.failureThreshold"] = "2",
            ["resilience.circuit.cooldownMs"] = "60000",
        };
        var ctx = new PlusActivationContext
        {
            ProxyServer = proxy,
            ClusterManager = manager,
            Options = options,
            Middleware = new List<IProxyMiddleware>(),
        };
        using var circuit = CircuitBreakerController.TryStart(ctx, options)!;
        circuit.RecordStatusForTests("d1", 500);
        Assert.AreEqual(DestinationState.Healthy, manager.Snapshot.DestinationStates["d1"]);
        circuit.RecordStatusForTests("d1", 503);
        Assert.AreEqual(DestinationState.Unhealthy, manager.Snapshot.DestinationStates["d1"]);
        // Success resets consecutive-failure counter but does not clear ejection until cooldown.
        circuit.RecordStatusForTests("d1", 200);
        Assert.AreEqual(DestinationState.Unhealthy, manager.Snapshot.DestinationStates["d1"]);
    }
}
