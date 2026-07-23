using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Reproduces a real-world HTTP/2 failure (browser reports <c>ERR_HTTP2_PROTOCOL_ERROR</c>) seen when
///     browsing to an external HTTP/2 site (e.g. https://www.google.com/) through the proxy with TLS
///     decryption on and <see cref="ProxyServer.EnableHttp2" /> at its new default of <c>true</c>. Unlike the
///     rest of the HTTP/2 suite, which relays to a local Kestrel <c>TestServer</c>, this hits a real external
///     origin so the proxy's relay has to cope with whatever frame patterns/extensions a production HTTP/2
///     server actually sends (e.g. additional SETTINGS parameters, larger header blocks, server-initiated
///     WINDOW_UPDATE/PING cadence) that a local test server may never exercise. Requires outbound internet
///     access; skips (does not fail) if the origin cannot be reached at all.
/// </summary>
[TestClass]
public class Http2ExternalSiteTests
{
    private const string TargetUrl = "https://www.google.com/";

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Real_External_Site_Request_Through_Decrypting_Proxy_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableHttp2 = true;

        var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0);
        proxy.AddEndPoint(explicitEndPoint);
        proxy.Start();

        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
                UseProxy = true,
                SslOptions =
                {
                    RemoteCertificateValidationCallback =
                        (_, certificate, _, errors) => ValidateAgainstProxyRoot(proxy, certificate, errors)
                }
            };

            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = new Version(2, 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(20)
            };

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(TargetUrl);
            }
            catch (HttpRequestException ex) when (IsUnreachable(ex))
            {
                Assert.Inconclusive($"Could not reach {TargetUrl} from the test environment: {ex.Message}");
                return;
            }

            using (response)
            {
                Assert.IsTrue((int)response.StatusCode < 500,
                    $"Expected a non-server-error response, got {(int)response.StatusCode}.");

                // Force the body to be read so any mid-stream protocol violation (e.g. the
                // ERR_HTTP2_PROTOCOL_ERROR reported by browsers) surfaces here instead of being silently
                // dropped.
                var body = await response.Content.ReadAsStringAsync();
                Assert.IsFalse(string.IsNullOrEmpty(body), "Expected a non-empty response body.");
            }
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Http2_Real_External_Site_Many_Concurrent_And_Sequential_Requests_Through_Decrypting_Proxy_Succeed()
    {
        // A browser loading a real page opens many concurrent streams on the same HTTP/2 connection (main
        // document + subresources) and then issues further requests re-using that same connection (including
        // any Set-Cookie the origin sent back) - reproduces ERR_HTTP2_PROTOCOL_ERROR seen with a real browser
        // that a single sequential GET (see the other test in this class) does not.
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableHttp2 = true;

        var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0);
        proxy.AddEndPoint(explicitEndPoint);
        proxy.Start();

        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
                UseProxy = true,
                SslOptions =
                {
                    RemoteCertificateValidationCallback =
                        (_, certificate, _, errors) => ValidateAgainstProxyRoot(proxy, certificate, errors)
                }
            };

            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = new Version(2, 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(20)
            };

            string[] paths = { "/", "/favicon.ico", "/robots.txt", "/", "/robots.txt" };

            try
            {
                // First: many concurrent streams on one freshly-established connection.
                var concurrentTasks = new System.Collections.Generic.List<Task<HttpResponseMessage>>();
                foreach (var path in paths)
                {
                    concurrentTasks.Add(client.GetAsync(TargetUrl.TrimEnd('/') + path));
                }

                var responses = await Task.WhenAll(concurrentTasks);
                foreach (var response in responses)
                {
                    using (response)
                    {
                        Assert.IsTrue((int)response.StatusCode < 500,
                            $"Expected a non-server-error response, got {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}.");
                        await response.Content.ReadAsByteArrayAsync();
                    }
                }

                // Then: several more sequential requests re-using the same connection (and any cookies).
                for (var i = 0; i < 5; i++)
                {
                    using var response = await client.GetAsync(TargetUrl);
                    Assert.IsTrue((int)response.StatusCode < 500,
                        $"Sequential request #{i} got {(int)response.StatusCode}.");
                    await response.Content.ReadAsByteArrayAsync();
                }
            }
            catch (HttpRequestException ex) when (IsUnreachable(ex))
            {
                Assert.Inconclusive($"Could not reach {TargetUrl} from the test environment: {ex.Message}");
            }
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static bool IsUnreachable(HttpRequestException ex)
    {
        return ex.InnerException is System.Net.Sockets.SocketException;
    }

    private static bool ValidateAgainstProxyRoot(ProxyServer proxy, X509Certificate certificate,
        SslPolicyErrors sslPolicyErrors)
    {
        const SslPolicyErrors fatalErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable;

        if (certificate == null || (sslPolicyErrors & fatalErrors) != SslPolicyErrors.None) return false;

        var rootCertificate = proxy.CertificateManager.RootCertificate;
        if (rootCertificate == null) return false;

        var loadedCertificate = certificate as X509Certificate2;
        var disposeCertificate = loadedCertificate == null;
        loadedCertificate ??= X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(loadedCertificate);
        }
        finally
        {
            if (disposeCertificate) loadedCertificate.Dispose();
        }
    }
}
