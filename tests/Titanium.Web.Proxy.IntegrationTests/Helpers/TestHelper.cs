using System;
using System.Net;
using System.Net.Http;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

public static class TestHelper
{
    public static HttpClient GetHttpClient(int localProxyPort,
        bool enableBasicProxyAuthorization = false)
    {
        var proxy = new TestProxy($"http://localhost:{localProxyPort}", enableBasicProxyAuthorization);

        var handler = CreateHandler(useProxy: true);
        handler.Proxy = proxy;

        return new HttpClient(handler);
    }

    /// <summary>
    ///     Direct (no proxy) client for reverse/transparent endpoint tests.
    ///     Always sets <see cref="HttpClientHandler.UseProxy"/> to <see langword="false"/> so a machine
    ///     WinINET/system proxy — e.g. the Basic example listening on localhost:8000 after
    ///     <c>dotnet run</c> — cannot intercept traffic meant for the in-process test proxy.
    /// </summary>
    public static HttpClient GetHttpClient()
    {
        return new HttpClient(CreateHandler(useProxy: false));
    }

    /// <summary>
    ///     An HttpClient forced onto HTTP/2 (via a fixed proxy and RequestVersionExact) for exercising the
    ///     proxy's HTTP/2 relay. A single instance reuses one underlying HTTP/2 connection (and therefore one
    ///     HPACK encoder/decoder pair on each leg) across multiple requests, which is what tests of
    ///     connection-scoped state (e.g. HPACK dynamic table reuse) need.
    /// </summary>
    public static HttpClient GetHttp2Client(ProxyServer proxy)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static HttpClientHandler CreateHandler(bool useProxy)
    {
        return new HttpClientHandler
        {
            // Default UseProxy=true would send reverse-proxy test traffic through the machine's
            // system proxy (often the Basic example on :8000), which MITMs with the product root
            // and breaks CustomRootTrust validation against the test CA.
            UseProxy = useProxy,
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
        };
    }

    public class TestProxy : IWebProxy
    {
        public TestProxy(string proxyUri, bool enableAuthorization)
            : this(new Uri(proxyUri))
        {
            if (enableAuthorization)
            {
                Credentials = new NetworkCredential("test", "Test56");
            }
        }

        private TestProxy(Uri proxyUri)
        {
            ProxyUri = proxyUri;
        }

        public Uri ProxyUri { get; set; }
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            return ProxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            return false;
        }
    }
}
