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

        var handler = CreateHandler();
        handler.Proxy = proxy;
        handler.UseProxy = true;

        return new HttpClient(handler);
    }

    public static HttpClient GetHttpClient()
    {
        return new HttpClient(CreateHandler());
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
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
        public ICredentials Credentials { get; set; }

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
