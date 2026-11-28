using System;
using System.Net.Http;
using System.Threading.Tasks;
using Titanium.Net.Http;

namespace HttpClientManagedStack;

/// <summary>Sends one HTTPS request on HTTP/1.1, HTTP/2, and HTTP/3 through <see cref="TitaniumHttpHandler"/>.</summary>
public static class Program
{
    /// <summary>Calls the URL from the first argument on each version.</summary>
    /// <param name="args">The HTTPS URL.</param>
    /// <returns>Zero when the HTTP/1.1 call succeeds.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out Uri? address) || address.Scheme != Uri.UriSchemeHttps)
        {
            Console.WriteLine("Usage: HttpClientManagedStack <https-url>");
            return 1;
        }

        using TitaniumHttpHandler handler = new();
        using HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        bool http11 = await CallAsync(client, address, new Version(1, 1), HttpVersionPolicy.RequestVersionExact).ConfigureAwait(false);
        await CallAsync(client, address, new Version(2, 0), HttpVersionPolicy.RequestVersionExact).ConfigureAwait(false);
        await CallAsync(client, address, new Version(3, 0), HttpVersionPolicy.RequestVersionExact).ConfigureAwait(false);
        return http11 ? 0 : 1;
    }

    private static async Task<bool> CallAsync(HttpClient client, Uri address, Version version, HttpVersionPolicy policy)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address)
            {
                Version = version,
                VersionPolicy = policy,
            };
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine(version.ToString() + " " + ((int)response.StatusCode).ToString() + " " + body.Length.ToString() + " bytes");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine(version.ToString() + " failed: " + ex.Message);
            return false;
        }
    }
}
