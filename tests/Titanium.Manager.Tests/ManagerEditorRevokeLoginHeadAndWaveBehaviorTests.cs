using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     Manager leftovers toward 90%: editor cannot revoke agent tokens, invalid revoke id,
///     blank token lookups, UI HEAD login / logout-without-session, and publish waves with
///     non-string matchLabels plus malformed JSON fallback.
/// </summary>
[TestClass]
public class ManagerEditorRevokeLoginHeadAndWaveBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-ed-{Guid.NewGuid():N}.db");

    private const string Yaml = """
        listeners:
          - port: 8000
            type: explicit
        routes: []
        clusters: []
        """;

    [TestMethod]
    public async Task Api_Editor_CannotRevokeAgentToken_AndInvalidIdIs400()
    {
        // Bug class: Require(Admin) must surface 403 for editors, not 500.
        var dbPath = TempDb();
        var adminPlain = "admin-" + Guid.NewGuid().ToString("N");
        var editorPlain = "editor-" + Guid.NewGuid().ToString("N");
        await using var host = await StartWithRolesAsync(dbPath, adminPlain, ("editor-user", editorPlain, "editor"));

        using var admin = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        admin.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + adminPlain);
        var fleetResp = await admin.PostAsync("/v1/fleets", ManagerTestHost.Json(new { name = "edge" }));
        fleetResp.EnsureSuccessStatusCode();
        using var fleetDoc = await ManagerTestHost.ReadJsonAsync(fleetResp);
        var fleetId = fleetDoc.RootElement.GetProperty("id").GetInt64();

        var tokenResp = await admin.PostAsync(
            "/v1/agent-tokens",
            ManagerTestHost.Json(new { fleetId, name = "node" }));
        tokenResp.EnsureSuccessStatusCode();
        using var tokenDoc = await ManagerTestHost.ReadJsonAsync(tokenResp);
        var tokenId = tokenDoc.RootElement.GetProperty("id").GetInt64();

        using var editor = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        editor.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + editorPlain);
        var forbidden = await editor.PostAsync(
            $"/v1/agent-tokens/{tokenId}/revoke",
            new StringContent(""));
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var badId = await admin.PostAsync("/v1/agent-tokens/not-a-number/revoke", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.BadRequest, badId.StatusCode);

        var missing = await admin.PostAsync("/v1/agent-tokens/999999/revoke", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public void Store_BlankTokensAndMissingFleet_StillNullOrThrow()
    {
        using var store = new ManagerStore(TempDb());
        Assert.IsNull(store.FindAdminByToken(""));
        Assert.IsNull(store.FindAdminByToken(null!));
        Assert.IsNull(store.FindAgentByToken(""));
        Assert.IsNull(store.FindAgentByToken(null!));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateRevision(77, Yaml, "a"));
        Assert.ThrowsExactly<KeyNotFoundException>(() => store.CreateAgentToken(77, "t"));
    }

    [TestMethod]
    public async Task Ui_Login_HeadAndPut_AndLogoutWithoutSession()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();

        using var head = new HttpRequestMessage(HttpMethod.Head, "/login");
        var headResp = await client.SendAsync(head);
        Assert.AreEqual(HttpStatusCode.OK, headResp.StatusCode);
        Assert.AreEqual(0, (await headResp.Content.ReadAsByteArrayAsync()).Length);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/login");
        var putResp = await client.SendAsync(put);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, putResp.StatusCode);

        var logout = await client.PostAsync("/logout", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        Assert.AreEqual("ok", await logout.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Api_Publish_ParsesNonStringMatchLabels_AndMalformedJsonFallback()
    {
        await using var host = await ManagerTestHost.StartAsync();
        var fleet = host.Store.CreateFleet("waves");
        var rev = host.Store.CreateRevision(fleet.Id, Yaml, "author");
        using var client = host.CreateAdminClient();
        var body = """
            {
              "bakeSeconds": 5,
              "waves": [
                {
                  "matchLabels": { "tier": "edge", "n": 7 },
                  "maxUnavailable": 1,
                  "bakeSeconds": 2
                }
              ]
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var ok = await client.PostAsync($"/v1/fleets/{fleet.Id}/revisions/{rev.Id}/publish", content);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);

        var steps = host.Store.ListRolloutSteps(host.Store.ListRollouts(fleet.Id).Single().Id);
        Assert.AreEqual(1, steps.Count);
        StringAssert.Contains(steps[0].LabelSelectorJson, "\"n\":\"7\"");
        StringAssert.Contains(steps[0].LabelSelectorJson, "\"tier\":\"edge\"");
        Assert.AreEqual(2, steps[0].BakeSeconds);

        // Malformed JSON falls back to immediate publish; use a separate fleet so no active rollout blocks it.
        var fleet2 = host.Store.CreateFleet("waves-bad");
        var revBad = host.Store.CreateRevision(fleet2.Id, Yaml, "author");
        using var bad = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var fallback = await client.PostAsync($"/v1/fleets/{fleet2.Id}/revisions/{revBad.Id}/publish", bad);
        Assert.AreEqual(HttpStatusCode.OK, fallback.StatusCode);
    }

    private static async Task<OwnedHost> StartWithRolesAsync(
        string dbPath,
        string adminPlain,
        params (string Name, string Plain, string Role)[] extras)
    {
        var store = new ManagerStore(dbPath);
        store.EnsureBootstrapAdminToken(adminPlain);
        foreach (var (name, plain, role) in extras)
        {
            InsertAdminToken(dbPath, plain, name, role);
        }

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

    private sealed class OwnedHost(string dbPath, ManagerStore store, ManagerHttpServer server, string baseUrl)
        : IAsyncDisposable
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
                // Best-effort.
            }
        }
    }
}
