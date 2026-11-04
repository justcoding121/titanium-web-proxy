using System.Net;
using System.Net.Sockets;
using Titanium.Manager.Api;
using Titanium.Manager.Store;

namespace Titanium.E2E.Tests.UiManager;

internal sealed class ManagerUiPlaywrightHost : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ManagerStore _store;
    private readonly ManagerHttpServer _server;

    private ManagerUiPlaywrightHost(string dbPath, ManagerStore store, ManagerHttpServer server, string adminToken)
    {
        _dbPath = dbPath;
        _store = store;
        _server = server;
        AdminToken = adminToken;
    }

    public string BaseUrl => _server.BaseUrl;

    public string AdminToken { get; }

    public static async Task<ManagerUiPlaywrightHost> StartAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"twp-manager-pw-{Guid.NewGuid():N}.db");
        var admin = "pw-admin-token-" + Guid.NewGuid().ToString("N");
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
                return new ManagerUiPlaywrightHost(dbPath, store, server, admin);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }

        store.Dispose();
        throw new InvalidOperationException("Failed to bind manager Playwright host.", last);
    }

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
