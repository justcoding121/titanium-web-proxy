using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Fat Manager leftovers: UI security headers, bootstrap token edges, viewer publish forbid.
/// </summary>
[TestClass]
public class ManagerSecurityHeadersBootstrapViewerBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-sec-{Guid.NewGuid():N}.db");

    [TestMethod]
    public async Task UiResponses_CarrySecurityHeaders()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();

        var login = await client.GetAsync("/login");
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        AssertSecurityHeaders(login);

        var ok = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = host.AdminToken,
        }));
        Assert.AreEqual(HttpStatusCode.SeeOther, ok.StatusCode);
        AssertSecurityHeaders(ok);

        var home = await client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, home.StatusCode);
        AssertSecurityHeaders(home);
    }

    [TestMethod]
    public void EnsureBootstrapAdminToken_IgnoresBlank_AndIsIdempotent()
    {
        var path = TempDb();
        using var store = new ManagerStore(path);
        store.EnsureBootstrapAdminToken("   ");
        store.EnsureBootstrapAdminToken("");
        Assert.IsNull(store.FindAdminByToken("   "));

        var token = "bootstrap-" + Guid.NewGuid().ToString("N");
        store.EnsureBootstrapAdminToken(token, "first");
        store.EnsureBootstrapAdminToken(token, "second");
        var row = store.FindAdminByToken(token);
        Assert.IsNotNull(row);
        Assert.AreEqual("first", row!.Name);
        Assert.AreEqual("admin", row.Role);
    }

    [TestMethod]
    public async Task Api_ViewerToken_CanListFleets_ButCannotPublish()
    {
        var dbPath = TempDb();
        var adminPlain = "admin-" + Guid.NewGuid().ToString("N");
        var viewerPlain = "viewer-" + Guid.NewGuid().ToString("N");
        await using var host = await StartWithViewerAsync(dbPath, adminPlain, viewerPlain);

        using var viewer = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        viewer.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + viewerPlain);

        var fleets = await viewer.GetAsync("/v1/fleets");
        Assert.AreEqual(HttpStatusCode.OK, fleets.StatusCode);

        var create = await viewer.PostAsync(
            "/v1/fleets",
            new StringContent("""{"name":"nope"}""", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Forbidden, create.StatusCode);

        using var admin = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        admin.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + adminPlain);

        var fleetResp = await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "pub" }));
        fleetResp.EnsureSuccessStatusCode();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(fleetResp);
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var revResp = await admin.PostAsync(
            $"/v1/fleets/{fleetId}/revisions",
            ManagerTestHost.Yaml("""
                listeners:
                  - port: 8000
                    type: explicit
                """));
        revResp.EnsureSuccessStatusCode();
        using var revDoc = await ManagerTestHost.ReadJsonAsync(revResp);
        var revId = revDoc.RootElement.GetProperty("id").GetInt64();

        var publish = await viewer.PostAsync(
            $"/v1/fleets/{fleetId}/revisions/{revId}/publish",
            new StringContent(""));
        Assert.AreEqual(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ManagerUiHost.SessionCookie));
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.IsTrue(response.Headers.TryGetValues("Content-Security-Policy", out var csp) ||
                      response.Content.Headers.TryGetValues("Content-Security-Policy", out csp));
        Assert.IsTrue(csp.Any(v => v.Contains("default-src", StringComparison.Ordinal)));

        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("no-store", Header(response, "Cache-Control"));
        Assert.AreEqual("no-referrer", Header(response, "Referrer-Policy"));
        Assert.AreEqual("DENY", Header(response, "X-Frame-Options"));
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) ||
            response.Content.Headers.TryGetValues(name, out values))
        {
            return values.Single();
        }

        Assert.Fail($"missing header {name}");
        return "";
    }

    private static async Task<OwnedHost> StartWithViewerAsync(string dbPath, string adminPlain, string viewerPlain)
    {
        var store = new ManagerStore(dbPath);
        store.EnsureBootstrapAdminToken(adminPlain);
        InsertAdminToken(dbPath, viewerPlain, "viewer-user", "viewer");

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
                return new OwnedHost(dbPath, store, server, prefix);
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

    private static void InsertAdminToken(string dbPath, string plain, string name, string role)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO admin_tokens (name, token_hash, role, created_at)
            VALUES ($n, $h, $r, $t);
            """;
        insert.Parameters.AddWithValue("$n", name);
        insert.Parameters.AddWithValue("$h", TokenHasher.Hash(plain));
        insert.Parameters.AddWithValue("$r", role);
        insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class OwnedHost(string dbPath, ManagerStore store, ManagerHttpServer server, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;

        public async ValueTask DisposeAsync()
        {
            await server.DisposeAsync().ConfigureAwait(false);
            store.Dispose();
            try
            {
                File.Delete(dbPath);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
