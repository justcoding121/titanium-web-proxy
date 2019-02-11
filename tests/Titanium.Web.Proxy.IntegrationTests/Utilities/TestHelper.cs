using System;
using System.Net;
using System.Net.Http;

namespace Titanium.Web.Proxy.IntegrationTests
{
    public static class TestHelper
    {
        public static HttpClient CreateHttpClient(int localProxyPort)
        {
            var proxy = new TestProxy($"http://localhost:{localProxyPort}");

            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true
            };

            return new HttpClient(handler);
        }

        public class TestProxy : IWebProxy
        {
            public Uri ProxyUri { get; set; }
            public ICredentials Credentials { get; set; }

            public TestProxy(string proxyUri)
                : this(new Uri(proxyUri))
            {
            }

            public TestProxy(Uri proxyUri)
            {
                this.ProxyUri = proxyUri;
            }

            public Uri GetProxy(Uri destination)
            {
                return this.ProxyUri;
            }

            public bool IsBypassed(Uri host)
            {
                return false;
            }

        }
    }
}
