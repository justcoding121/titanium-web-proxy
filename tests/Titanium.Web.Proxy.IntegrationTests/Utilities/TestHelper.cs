using System.Net;
using System.Net.Http;

namespace Titanium.Web.Proxy.IntegrationTests
{
    public static class TestHelper
    {
        public static HttpClient CreateHttpClient(string url, int localProxyPort)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://localhost:{localProxyPort}", false),
                UseProxy = true
            };

            return new HttpClient(handler);
        }
    }
}
