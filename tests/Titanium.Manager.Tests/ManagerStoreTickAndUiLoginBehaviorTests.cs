using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Fat leftovers: TickRollouts wave advance and UI login FormValue for secret= token field.
/// </summary>
[TestClass]
public class ManagerStoreTickAndUiLoginBehaviorTests
{
    private const string YamlV1 = """
        listeners:
          - port: 8000
            type: explicit
        """;

    private const string YamlV2 = """
        listeners:
          - port: 8001
            type: explicit
        """;

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-tick-{Guid.NewGuid():N}.db");

    [TestMethod]
    public void TickRollouts_AdvancesWaveWhenReadyAndBakeIsZero()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("tick");
        var first = store.CreateRevision(fleet.Id, YamlV1, "a");
        store.Publish(fleet.Id, first.Id);
        store.EnsureAgent(fleet.Id, "w1", """{"wave":"1"}""");
        store.EnsureAgent(fleet.Id, "w2", """{"wave":"2"}""");

        var second = store.CreateRevision(fleet.Id, YamlV2, "a");
        var rollout = store.Publish(
            fleet.Id,
            second.Id,
            [
                new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "1" }, 0, 0),
                new PublishWaveSpec(new Dictionary<string, string> { ["wave"] = "2" }, 0, 0),
            ]);

        Assert.AreEqual(second.ContentHash, store.GetConfigForAgent(fleet.Id, "w1")!.ContentHash);
        Assert.AreEqual(first.ContentHash, store.GetConfigForAgent(fleet.Id, "w2")!.ContentHash);

        // A successful ack advances the wave when bakeSeconds is 0; TickRollouts is the
        // same path used by the HTTP tick loop when no ack arrives.
        store.UpsertAgentAck(fleet.Id, "w1", second.ContentHash, ok: true, error: null, healthJson: """{"status":"healthy"}""");
        _ = store.TickRollouts();

        Assert.AreEqual(second.ContentHash, store.GetConfigForAgent(fleet.Id, "w2")!.ContentHash,
            "wave-2 should be exposed after wave-1 bakes with bakeSeconds=0");
        Assert.AreEqual("running", store.GetRollout(fleet.Id, rollout.Id)!.State);
    }

    [TestMethod]
    public async Task UiLogin_AcceptsSecretFormField_AndRejectsBlank()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();

        var blank = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = "   ",
        }));
        Assert.AreEqual(HttpStatusCode.Unauthorized, blank.StatusCode);

        var wrong = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = "not-the-token",
        }));
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var ok = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = host.AdminToken,
        }));
        Assert.AreEqual(HttpStatusCode.SeeOther, ok.StatusCode);
        Assert.AreEqual("/", ok.Headers.Location?.OriginalString);
        Assert.IsTrue(ok.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.IsTrue(cookies.Any(c => c.StartsWith(ManagerUiHost.SessionCookie + "=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UiApi_RejectsMissingCsrf_With403()
    {
        await using var host = await ManagerTestHost.StartAsync();
        using var client = host.CreateCookieClient();
        var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = host.AdminToken,
        }));
        Assert.AreEqual(HttpStatusCode.SeeOther, login.StatusCode);

        var resp = await client.PostAsync(
            "/ui/fleets",
            new StringContent("""{"name":"x"}""", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "csrf");
    }
}
