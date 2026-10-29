using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Titanium.Manager.Api;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

internal sealed class ManagerTestHost : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ManagerStore _store;
    private readonly ManagerHttpServer _server;
    private readonly string _adminToken;

    private ManagerTestHost(string dbPath, ManagerStore store, ManagerHttpServer server, string adminToken)
    {
        _dbPath = dbPath;
        _store = store;
        _server = server;
        _adminToken = adminToken;
    }

    public string BaseUrl => _server.BaseUrl;
    public string AdminToken => _adminToken;
    public ManagerStore Store => _store;

    public static async Task<ManagerTestHost> StartAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"twp-manager-{Guid.NewGuid():N}.db");
        var admin = "test-admin-token-" + Guid.NewGuid().ToString("N");
        var store = new ManagerStore(dbPath);
        store.EnsureBootstrapAdminToken(admin);

        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = FreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var server = new ManagerHttpServer(store, prefix);
            try
            {
                server.Start();
                await Task.Yield();
                return new ManagerTestHost(dbPath, store, server, admin);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }

        store.Dispose();
        throw new InvalidOperationException("Failed to bind manager test host.", last);
    }

    public HttpClient CreateAdminClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + _adminToken);
        return client;
    }

    public HttpClient CreateAgentClient(string agentToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + agentToken);
        return client;
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static StringContent Yaml(string yaml) =>
        new(yaml, Encoding.UTF8, "application/x-yaml");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            /* ignore */
        }
    }
}
