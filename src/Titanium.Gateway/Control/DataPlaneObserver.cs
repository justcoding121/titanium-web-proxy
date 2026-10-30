using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;

namespace Titanium.Gateway.Control;

/// <summary>Reads what the data-plane pods are running, to confirm a Gateway is programmed.</summary>
public interface IDataPlaneObserver
{
    /// <summary>Asks every address for <c>/readyz</c> and <c>/version</c>. One entry per address, in order.</summary>
    Task<DataPlaneObservation> ObserveAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken);
}

/// <summary>
///     Polls each ready data-plane pod's admin listener. A pod counts only when <c>/readyz</c> is 200 and
///     <c>/version</c> (bearer token) reports a <c>config_hash</c>; the controller compares that with the hash of the
///     config it wrote.
/// </summary>
public sealed class HttpDataPlaneObserver : IDataPlaneObserver
{
    private const int MaxPods = 64;

    private readonly HttpClient _http;
    private readonly int _adminPort;
    private readonly string? _token;
    private readonly ILogger _log;

    public HttpDataPlaneObserver(HttpClient http, int adminPort, string? token, ILogger log)
    {
        _http = http;
        _adminPort = adminPort;
        _token = token;
        _log = log;
    }

    public async Task<DataPlaneObservation> ObserveAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return DataPlaneObservation.None;
        }

        var hashes = await Task.WhenAll(addresses.Take(MaxPods).Select(a => ProbeAsync(a, cancellationToken))).ConfigureAwait(false);
        return new DataPlaneObservation(hashes);
    }

    private async Task<string?> ProbeAsync(string address, CancellationToken cancellationToken)
    {
        var host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        // In-cluster admin probes are loopback/pod-network HTTP by design (no TLS on the admin port).
        var baseUri = $"http://{host}:{_adminPort}"; // NOSONAR S5332 -- gateway admin port is cleartext by contract.
        try
        {
            using var ready = await _http.GetAsync(baseUri + "/readyz", cancellationToken).ConfigureAwait(false);
            if (!ready.IsSuccessStatusCode)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri + "/version");
            if (!string.IsNullOrEmpty(_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            using var version = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!version.IsSuccessStatusCode)
            {
                return null;
            }

            await using var body = await version.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.GetString("config_hash");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Data-plane pod {Address} did not answer: {Message}", address, ex.Message);
            return null;
        }
    }
}
