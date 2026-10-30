using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.TestKit;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Plus.Tests;

/// <summary>A protected application: records what the proxy forwards, and sets a cookie of its own.</summary>
internal sealed class OidcOrigin : IAsyncDisposable
{
    private readonly IWebHost _host;
    private int _hits;

    public OidcOrigin()
    {
        _host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(async context =>
            {
                Interlocked.Increment(ref _hits);
                var headers = context.Request.Headers.ToDictionary(
                    static h => h.Key, static h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new OriginRequest(context.Request.Method, context.Request.Path + context.Request.QueryString, headers));
                context.Response.Headers.Append("Set-Cookie", "app=1; Path=/");
                await context.Response.WriteAsync("origin-ok");
            }))
            .Build();
    }

    public int Port { get; private set; }

    public int Hits => Volatile.Read(ref _hits);

    public ConcurrentQueue<OriginRequest> Requests { get; } = new();

    public OriginRequest Last => Requests.Last();

    public async Task StartAsync()
    {
        await _host.StartAsync();
        Port = new Uri(_host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal sealed record OriginRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What a test may change in the policy. Defaults are a working login with claim headers.</summary>
internal sealed class OidcPolicyOptions
{
    public string? RedirectPath { get; set; }

    public string? PublicBaseUrl { get; set; }

    public string CookieKeys { get; set; } = Convert.ToHexString(Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray());

    public string? CookieName { get; set; }

    public string[]? AllowedEmails { get; set; }

    public string[]? AllowedEmailDomains { get; set; }

    public string[]? AllowedGroups { get; set; }

    public string? GroupsClaim { get; set; }

    public string[]? Allowlist { get; set; }

    public bool Refresh { get; set; }

    public int? SessionTtlSeconds { get; set; }

    public bool MissingClientSecret { get; set; }

    public RoutePolicyConfig Build(string issuer) => new()
    {
        Name = "login",
        Kind = "oidc",
        Issuer = issuer,
        ClientId = FakeIdp.ClientId,
        ClientSecretEnv = "OIDC_CLIENT_SECRET",
        CookieSecretEnv = "OIDC_COOKIE_KEY",
        RedirectPath = RedirectPath,
        PublicBaseUrl = PublicBaseUrl,
        CookieName = CookieName,
        LogoutPath = "/logout",
        SessionTtlSeconds = SessionTtlSeconds,
        AllowedEmails = AllowedEmails,
        AllowedEmailDomains = AllowedEmailDomains,
        AllowedGroups = AllowedGroups,
        GroupsClaim = GroupsClaim,
        PostLoginRedirectAllowlist = Allowlist,
        RefreshTokens = Refresh ? true : null,
        PassClaims = new Dictionary<string, string>
        {
            ["email"] = "X-User-Email",
            ["sub"] = "X-User-Id",
            ["name"] = "X-User-Name",
        },
    };
}

/// <summary>An identity provider, a protected origin, and a proxy with the <c>oidc</c> policy in front.</summary>
internal sealed class OidcHarness : IAsyncDisposable
{
    public const string Host = "matrix.test";

    private ProxyServer? _proxy;

    private OidcHarness(OidcClock clock, FakeIdp idp, OidcOrigin origin, OidcPolicyHandler? handler, OidcPolicyOptions options)
    {
        Clock = clock;
        Idp = idp;
        Origin = origin;
        Handler = handler;
        Options = options;
    }

    public OidcClock Clock { get; }

    public FakeIdp Idp { get; }

    public OidcOrigin Origin { get; }

    public OidcPolicyHandler? Handler { get; }

    public OidcPolicyOptions Options { get; }

    public RoutePolicyConfig Policy { get; private set; } = null!;

    public int ProxyPort => _proxy!.ProxyEndPoints[0].Port;

    public static async Task<OidcHarness> StartAsync(Action<OidcPolicyOptions>? configure = null, bool registerHandler = true)
    {
        var clock = new OidcClock();
        var options = new OidcPolicyOptions();
        configure?.Invoke(options);
        var idp = await FakeIdp.StartAsync(clock);
        var origin = new OidcOrigin();
        await origin.StartAsync();

        OidcPolicyHandler? handler = null;
        if (registerHandler)
        {
            handler = new OidcPolicyHandler(logger: null, time: clock)
            {
                ReadEnvironment = name => name switch
                {
                    "OIDC_CLIENT_SECRET" => options.MissingClientSecret ? null : FakeIdp.ClientSecret,
                    "OIDC_COOKIE_KEY" => options.CookieKeys,
                    _ => null,
                },
            };
        }

        var harness = new OidcHarness(clock, idp, origin, handler, options);
        await harness.StartProxyAsync();
        return harness;
    }

    private async Task StartProxyAsync()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = Origin.Port }],
            },
        ]);

        Policy = Options.Build(Idp.Issuer);
        _proxy = new ProxyServer(false, false, false);
        _proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Policies = ["login"],
                },
            ],
            Policies = [Policy],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            PolicyHandlers = Handler is null ? null : [Handler],
        };
        _proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        _proxy.Start();
    }

    public Browser NewBrowser() => new(ProxyPort);

    /// <summary>Asks for a page with no session: the proxy answers with the sign-in redirect.</summary>
    public static async Task<string> StartLoginAsync(Browser browser, string path = "/app?x=1")
    {
        using var response = await browser.GetAsync($"http://{Host}{path}", ("Accept", "text/html"));
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>Follows the authorize redirect the way a browser would and returns where the provider sends it next.</summary>
    public static async Task<string> VisitProviderAsync(Browser browser, string authorizeUrl)
    {
        using var response = await browser.GetDirectAsync(authorizeUrl);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode, "the provider refused the authorize request");
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>The whole sign-in. Returns the callback response (a redirect back to the page when it works).</summary>
    public async Task<HttpResponseMessage> SignInAsync(Browser browser, string path = "/app?x=1") // NOSONAR CA1822 -- Test harness keeps sign-in on the fixture instance for readable call sites.
    {
        var authorize = await StartLoginAsync(browser, path);
        var callback = await VisitProviderAsync(browser, authorize);
        return await browser.GetAsync(callback, ("Accept", "text/html"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null)
        {
            if (_proxy.ProxyRunning)
            {
                await _proxy.StopAsync();
            }

            _proxy.Dispose();
        }

        await Origin.DisposeAsync();
        await Idp.DisposeAsync();
    }
}

/// <summary>A client with a cookie jar that talks to the proxy as an explicit proxy, and to the provider directly.</summary>
internal sealed class Browser : IDisposable
{
    private readonly HttpClient _viaProxy;
    private readonly HttpClient _direct;

    public Browser(int proxyPort)
    {
        _viaProxy = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _direct = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw Set-Cookie lines of the last response from the proxy.</summary>
    public List<string> LastSetCookies { get; private set; } = [];

    public Task<HttpResponseMessage> GetAsync(string url, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Get, url, headers);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(static c => c.Key + "=" + c.Value)));
        }

        var response = await _viaProxy.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var lines) ? lines.ToList() : [];
        foreach (var line in LastSetCookies)
        {
            Store(line);
        }

        return response;
    }

    /// <summary>A request to the provider: no proxy, no cookies.</summary>
    public Task<HttpResponseMessage> GetDirectAsync(string url) => _direct.GetAsync(url);

    public void Dispose()
    {
        _viaProxy.Dispose();
        _direct.Dispose();
    }

    private void Store(string line)
    {
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var pair = parts[0];
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return;
        }

        var name = pair[..eq];
        var value = pair[(eq + 1)..];
        var expired = value.Length == 0 || parts.Skip(1).Any(static p => p.Equals("Max-Age=0", StringComparison.OrdinalIgnoreCase));
        if (expired)
        {
            Cookies.Remove(name);
        }
        else
        {
            Cookies[name] = value;
        }
    }

    /// <summary>The cookie whose name starts with <paramref name="prefix"/> (the flow cookie has a derived name).</summary>
    public KeyValuePair<string, string>? CookieStartingWith(string prefix) =>
        Cookies.FirstOrDefault(c => c.Key.StartsWith(prefix, StringComparison.Ordinal)) is { Key: not null } found ? found : null;
}
