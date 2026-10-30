using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Security;
using Titanium.Plus.State;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>Hashed basic users, constant-time compares, auth throttle, audit trail, and control-plane TLS.</summary>
[TestClass]
public class PlusHardeningTests
{
    private const string Secret = "hardening-test-secret-value";
    private static readonly string[] PlainAndLegacyUsers = ["plain", "legacy"];
    private static readonly string[] PlaintextOnlyUser = ["plain"];
    private static readonly string[] AuditEntryFieldNames = ["time", "actor", "remote", "action", "target", "result"];

    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twp-plus-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    // ---- password hashes -------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(1, "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b")]
    [DataRow(4096, "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a")]
    public void Pbkdf2_MatchesKnownSha256Vectors(int iterations, string expectedHex)
    {
        var stored = Pbkdf2PasswordHash.Format("password", Encoding.ASCII.GetBytes("salt"), iterations, 32);

        var parts = stored["{pbkdf2}".Length..].Split('$');
        Assert.AreEqual(iterations.ToString(), parts[0]);
        Assert.AreEqual(expectedHex, Convert.ToHexString(Convert.FromBase64String(parts[2])).ToLowerInvariant());
    }

    [TestMethod]
    public void Pbkdf2_CreateThenVerify_UsesTheDefaultCost()
    {
        var stored = Pbkdf2PasswordHash.Create("correct horse");

        StringAssert.StartsWith(stored, "{pbkdf2}210000$");
        Assert.IsTrue(Pbkdf2PasswordHash.Verify("correct horse", stored));
        Assert.IsFalse(Pbkdf2PasswordHash.Verify("correct horsf", stored));
        Assert.IsFalse(Pbkdf2PasswordHash.Verify("", stored));
        Assert.AreNotEqual(stored, Pbkdf2PasswordHash.Create("correct horse"), "every hash gets its own salt");
    }

    [TestMethod]
    [DataRow("{pbkdf2}")]
    [DataRow("{pbkdf2}0$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==")]
    [DataRow("{pbkdf2}10000001$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==")]
    [DataRow("{pbkdf2}-5$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==")]
    [DataRow("{pbkdf2}10$not base64$aGFzaGhhc2hoYXNoaGFzaA==")]
    [DataRow("{pbkdf2}10$c2FsdA==$aGFzaGhhc2hoYXNoaGFzaA==")]
    [DataRow("{pbkdf2}10$c2FsdHNhbHRzYWx0$c2hvcnQ=")]
    [DataRow("{pbkdf2}10$c2FsdHNhbHRzYWx0")]
    public void Pbkdf2_RejectsMalformedHashes(string stored)
    {
        Assert.IsFalse(Pbkdf2PasswordHash.TryParse(stored, out _, out _, out _));
        Assert.IsFalse(Pbkdf2PasswordHash.Verify("anything", stored));
    }

    [TestMethod]
    [DataRow("$2a$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW")]
    [DataRow("$2b$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW")]
    public void Bcrypt_VerifiesAKnownVector(string hash)
    {
        var store = new BasicUserStore(new Dictionary<string, string> { ["alice"] = "{bcrypt}" + hash });

        Assert.IsTrue(store.Verify("alice", "U*U"));
        Assert.IsFalse(store.Verify("alice", "U*u"));
        Assert.IsFalse(store.Verify("alice", ""));
        Assert.AreEqual(0, store.PlaintextUsers.Count);
    }

    [TestMethod]
    public void Bcrypt_VerifiesAFreshlyGeneratedHash()
    {
        var hash = "{bcrypt}" + BCrypt.Net.BCrypt.HashPassword("s3cr3t-pass", workFactor: 4);
        var store = BasicUserStore.Parse("bob:" + hash);

        Assert.IsTrue(store.Verify("bob", "s3cr3t-pass"));
        Assert.IsFalse(store.Verify("bob", "s3cr3t-pasS"));
    }

    [TestMethod]
    public void BasicUserStore_MixesPlaintextAndHashes_AndReportsOnlyPlaintextNames()
    {
        var pbkdf2 = Pbkdf2PasswordHash.Format("pb-pass", RandomNumberGenerator.GetBytes(16), 1000, 32);
        var raw = $"plain:plain-pass,hashed:{pbkdf2},legacy:other-pass";

        var store = BasicUserStore.Parse(raw);

        Assert.AreEqual(3, store.Count);
        CollectionAssert.AreEquivalent(PlainAndLegacyUsers, store.PlaintextUsers.ToArray());
        Assert.IsTrue(store.Verify("plain", "plain-pass"));
        Assert.IsTrue(store.Verify("hashed", "pb-pass"));
        Assert.IsFalse(store.Verify("hashed", "plain-pass"));
        Assert.IsFalse(store.Verify("nobody", "pb-pass"));
        Assert.IsFalse(store.Verify("plain", "PLAIN-PASS"));
        foreach (var name in store.PlaintextUsers)
        {
            Assert.IsFalse(name.Contains("pass", StringComparison.Ordinal), "only user names are reported");
        }
    }

    [TestMethod]
    [DataRow("u:{bcrypt}notabcrypthash")]
    [DataRow("u:{bcrypt}$2b$05$short")]
    [DataRow("u:{bcrypt}")]
    [DataRow("u:{pbkdf2}garbage")]
    [DataRow("u:{pbkdf2}1$$")]
    public void BasicUserStore_RefusesAMalformedHash_NamingTheUserOnly(string raw)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => BasicUserStore.Parse(raw));

        StringAssert.Contains(ex.Message, "'u'");
        Assert.IsFalse(ex.Message.Contains("garbage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BasicUserStore_RemembersAHashedLogin_ButNotAFailure()
    {
        var clock = new ManualTimeProvider();
        var pbkdf2 = Pbkdf2PasswordHash.Format("pw", RandomNumberGenerator.GetBytes(16), 1000, 32);
        var store = new BasicUserStore(new Dictionary<string, string> { ["u"] = pbkdf2 }, clock);

        Assert.IsTrue(store.Verify("u", "pw"));
        Assert.IsTrue(store.Verify("u", "pw"));
        Assert.IsFalse(store.Verify("u", "pw2"));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsTrue(store.Verify("u", "pw"), "an expired memory re-verifies against the hash");
    }

    // ---- constant-time comparison ------------------------------------------------------------------------------

    [TestMethod]
    public void ConstantTime_StringEquals_HandlesLengthNullAndUnicode()
    {
        Assert.IsTrue(ConstantTime.StringEquals("abc", "abc"));
        Assert.IsTrue(ConstantTime.StringEquals("", ""));
        Assert.IsTrue(ConstantTime.StringEquals("pässwörd-日本", "pässwörd-日本"));
        Assert.IsFalse(ConstantTime.StringEquals("abc", "abd"));
        Assert.IsFalse(ConstantTime.StringEquals("abc", "abcd"));
        Assert.IsFalse(ConstantTime.StringEquals("abcd", "abc"));
        Assert.IsFalse(ConstantTime.StringEquals("ABC", "abc"));
        Assert.IsFalse(ConstantTime.StringEquals(null, "abc"));
        Assert.IsFalse(ConstantTime.StringEquals("abc", null));
        Assert.IsFalse(ConstantTime.StringEquals(null, null));
    }

    [TestMethod]
    public async Task ApiKeyMiddleware_ComparesEveryKey_AndAcceptsOnlyAConfiguredOne()
    {
        var keys = Enumerable.Range(0, 50).Select(i => $"key-{i:D3}-{new string('x', i)}").ToList();
        var mw = new ApiKeyBasicAuthMiddleware(keys, null);

        Assert.IsTrue(await Allows(mw, "key-000-"));
        Assert.IsTrue(await Allows(mw, keys[49]));
        Assert.IsTrue(await Allows(mw, keys[17]));
        Assert.IsFalse(await Allows(mw, keys[17] + "x"));
        Assert.IsFalse(await Allows(mw, keys[17][..^1]));
        Assert.IsFalse(await Allows(mw, ""));
    }

    [TestMethod]
    public async Task BasicAuthMiddleware_AcceptsHashedUsers_AndKeepsTheOldPlaintextBehaviour()
    {
        var hash = "{bcrypt}" + BCrypt.Net.BCrypt.HashPassword("hash-pass", workFactor: 4);
        var mw = new ApiKeyBasicAuthMiddleware(null, new Dictionary<string, string>
        {
            ["hashed"] = hash,
            ["plain"] = "plain-pass",
        });

        Assert.IsTrue(await BasicAllows(mw, "hashed", "hash-pass"));
        Assert.IsFalse(await BasicAllows(mw, "hashed", "plain-pass"));
        Assert.IsTrue(await BasicAllows(mw, "plain", "plain-pass"));
        Assert.IsFalse(await BasicAllows(mw, "plain", "hash-pass"));
        CollectionAssert.AreEqual(PlaintextOnlyUser, mw.PlaintextUsers.ToArray());
    }

    [TestMethod]
    public async Task BasicAuthMiddleware_ThrottlesARemoteThatKeepsFailing()
    {
        var clock = new ManualTimeProvider();
        var throttle = new AuthThrottle(timeProvider: clock);
        var mw = new ApiKeyBasicAuthMiddleware(
            null, new Dictionary<string, string> { ["u"] = "p" }, throttle: throttle);
        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 5555);

        for (var i = 0; i < 5; i++)
        {
            var failed = await Invoke(mw, "u", "wrong", remote);
            Assert.AreEqual(401, failed.HandledStatusCode);
        }

        var blocked = await Invoke(mw, "u", "p", remote);
        Assert.AreEqual(429, blocked.HandledStatusCode);
        Assert.AreEqual("60", blocked.HandledHeaders!.Single(h => h.Key == "Retry-After").Value);

        var other = await Invoke(mw, "u", "p", new IPEndPoint(IPAddress.Parse("203.0.113.10"), 1));
        Assert.IsFalse(other.IsHandled, "another remote is unaffected");

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.IsFalse((await Invoke(mw, "u", "p", remote)).IsHandled);
    }

    // ---- throttle ------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Throttle_BlocksAfterFiveFailuresInAMinute_AndReleasesOnTheManualClock()
    {
        var clock = new ManualTimeProvider();
        var throttle = new AuthThrottle(timeProvider: clock);

        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("10.0.0.1");
            Assert.IsFalse(throttle.IsBlocked("10.0.0.1", out _, out _), $"{i + 1} failures are still allowed");
        }

        throttle.RecordFailure("10.0.0.1");
        Assert.IsTrue(throttle.IsBlocked("10.0.0.1", out var retryAfter, out var first));
        Assert.AreEqual(60, retryAfter);
        Assert.IsTrue(first);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsTrue(throttle.IsBlocked("10.0.0.1", out retryAfter, out first));
        Assert.AreEqual(40, retryAfter);
        Assert.IsFalse(first, "a lockout is reported once");

        Assert.IsFalse(throttle.IsBlocked("10.0.0.2", out _, out _));

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.IsFalse(throttle.IsBlocked("10.0.0.1", out _, out _));
        Assert.AreEqual(0, throttle.TrackedRemotes, "an idle remote is forgotten");
    }

    [TestMethod]
    public void Throttle_IsASlidingWindow()
    {
        var clock = new ManualTimeProvider();
        var throttle = new AuthThrottle(timeProvider: clock);

        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("r");
        }

        clock.Advance(TimeSpan.FromSeconds(50));
        throttle.RecordFailure("r");
        Assert.IsTrue(throttle.IsBlocked("r", out var retryAfter, out _));
        Assert.AreEqual(10, retryAfter, "the oldest failure sets the release time");

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.IsFalse(throttle.IsBlocked("r", out _, out _), "four failures aged out; one is left");
    }

    [TestMethod]
    public void Throttle_CapsTheRemoteMap_DroppingExpiredThenStalest()
    {
        var clock = new ManualTimeProvider();
        var throttle = new AuthThrottle(maxRemotes: 3, timeProvider: clock);

        for (var i = 0; i < 5; i++)
        {
            throttle.RecordFailure("oldest");
        }

        Assert.IsTrue(throttle.IsBlocked("oldest", out _, out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.RecordFailure("b");
        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.RecordFailure("c");
        Assert.AreEqual(3, throttle.TrackedRemotes);

        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.RecordFailure("d");
        Assert.AreEqual(3, throttle.TrackedRemotes, "the map never grows past its cap");
        Assert.IsFalse(throttle.IsBlocked("oldest", out _, out _), "the stalest remote made room");

        clock.Advance(TimeSpan.FromMinutes(5));
        throttle.RecordFailure("e");
        Assert.AreEqual(1, throttle.TrackedRemotes, "expired entries are dropped before anything live");
    }

    [TestMethod]
    public void Throttle_ManyRemotes_StayBounded()
    {
        var throttle = new AuthThrottle(maxRemotes: 100);
        for (var i = 0; i < 1000; i++)
        {
            throttle.RecordFailure("198.51.100." + i);
        }

        Assert.IsTrue(throttle.TrackedRemotes <= 100);
    }

    // ---- audit log -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void AuditLog_WritesNdjsonWithTheDocumentedShape()
    {
        var clock = new ManualTimeProvider();
        var path = Path.Combine(_dir, "audit", "audit.ndjson");
        using (var audit = new AuditLog(path, timeProvider: clock))
        {
            audit.Record("dashboard:shared-secret", "10.1.2.3", AuditActions.Drain, "d1", AuditResults.Ok);
            clock.Advance(TimeSpan.FromSeconds(5));
            audit.Record("anonymous", "10.1.2.4", AuditActions.LoginFailed, "/v1/snapshot", AuditResults.Denied);
        }

        var lines = ReadLines(path);
        Assert.AreEqual(2, lines.Count);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            CollectionAssert.AreEqual(
                AuditEntryFieldNames,
                doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.IsTrue(DateTimeOffset.TryParse(doc.RootElement.GetProperty("time").GetString(), out _));
        }

        using var first = JsonDocument.Parse(lines[0]);
        Assert.AreEqual("dashboard:shared-secret", first.RootElement.GetProperty("actor").GetString());
        Assert.AreEqual("drain", first.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("d1", first.RootElement.GetProperty("target").GetString());
        Assert.AreEqual("ok", first.RootElement.GetProperty("result").GetString());
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(-5).UtcDateTime.ToString("O"), first.RootElement.GetProperty("time").GetString());
    }

    [TestMethod]
    public void AuditLog_Appends_AcrossRestarts_AndNeverRewritesHistory()
    {
        var path = Path.Combine(_dir, "audit.ndjson");
        using (var audit = new AuditLog(path))
        {
            audit.Record("a", "r", AuditActions.Login, "/", AuditResults.Ok);
        }

        using (var audit = new AuditLog(path))
        {
            audit.Record("b", "r", AuditActions.Login, "/", AuditResults.Ok);
        }

        var lines = ReadLines(path);
        Assert.AreEqual(2, lines.Count);
        StringAssert.Contains(lines[0], "\"actor\":\"a\"");
        StringAssert.Contains(lines[1], "\"actor\":\"b\"");
    }

    [TestMethod]
    public void AuditLog_RingKeepsTheNewest500_NewestFirst()
    {
        using var audit = new AuditLog();
        for (var i = 0; i < 650; i++)
        {
            audit.Record("a", "r", AuditActions.Drain, "t" + i, AuditResults.Ok);
        }

        var recent = audit.Recent();
        Assert.AreEqual(500, recent.Count);
        Assert.AreEqual("t649", recent[0].Target);
        Assert.AreEqual("t150", recent[^1].Target);
        Assert.AreEqual(3, audit.Recent(3).Count);
        Assert.AreEqual(0, audit.Recent(0).Count);
    }

    [TestMethod]
    public void AuditLog_RollsBySize_AndKeepsAtMostMaxFilesRolled()
    {
        var path = Path.Combine(_dir, "roll.ndjson");
        using (var audit = new AuditLog(path, maxFileBytes: 400, maxFiles: 2))
        {
            for (var i = 0; i < 40; i++)
            {
                audit.Record("actor", "10.0.0.1", AuditActions.Drain, "destination-" + i, AuditResults.Ok);
            }
        }

        Assert.IsTrue(File.Exists(path));
        Assert.IsTrue(File.Exists(path + ".1"));
        Assert.IsTrue(File.Exists(path + ".2"));
        Assert.IsFalse(File.Exists(path + ".3"), "older files are deleted");
        foreach (var file in new[] { path, path + ".1", path + ".2" })
        {
            Assert.IsTrue(new FileInfo(file).Length <= 400 + 200, file + " stays near the size limit");
            foreach (var line in ReadLines(file))
            {
                using var doc = JsonDocument.Parse(line);
            }
        }

        StringAssert.Contains(string.Join("\n", ReadLines(path)), "destination-39");
    }

    [TestMethod]
    public void AuditLog_TrimsAndCleansFields()
    {
        using var audit = new AuditLog();
        var entry = audit.Record("act\nor", "r", "drain", new string('t', 1000) + "\r\nX", "ok");

        Assert.AreEqual("act or", entry.Actor);
        Assert.AreEqual(256, entry.Target.Length);
        var empty = audit.Record("", null, "drain", null, "ok");
        Assert.AreEqual("anonymous", empty.Actor);
        Assert.AreEqual("unknown", empty.Remote);
        Assert.AreEqual("-", empty.Target);
    }

    [TestMethod]
    public void AuditLog_AFileFailureIsCounted_AndNeverThrows()
    {
        var path = Path.Combine(_dir, "blocked.ndjson");
        var errors = new List<string>();
        using var audit = new AuditLog(path, onError: errors.Add);
        // Close the log's handle, then lock the file exclusively so the next append cannot reopen it.
        audit.Dispose();
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        audit.Record("a", "r", AuditActions.Login, "/", AuditResults.Ok);
        audit.Record("a", "r", AuditActions.Login, "/", AuditResults.Ok);

        Assert.AreEqual(2, audit.WriteFailures);
        Assert.AreEqual(1, errors.Count, "reported once");
        Assert.AreEqual(2, audit.Recent().Count, "the ring still has the events");
    }

    // ---- control plane: audit, throttle, secrets never logged ------------------------------------------------

    [TestMethod]
    public async Task ControlPlane_AuditsMutationsAndFailedLogins_WithoutAnySecret()
    {
        var clock = new ManualTimeProvider();
        var auditPath = Path.Combine(_dir, "cp-audit.ndjson");
        using var audit = new AuditLog(auditPath, timeProvider: clock);
        var security = new ControlPlaneSecurity(audit, new AuthThrottle(timeProvider: clock));
        var manager = new ClusterManager();
        var cache = new CountingCache();
        using var server = new ControlPlaneServer(
            manager, "127.0.0.1", 0, Secret, new List<RouteConfig>(), null, cache, security);
        server.Start();
        using var http = new HttpClient();

        var wrongSecret = "attacker-guess-0123456789";
        using (var bad = Request(HttpMethod.Get, server.Prefix + "v1/snapshot", wrongSecret))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);
        }

        using (var put = Request(HttpMethod.Put, server.Prefix + "v1/snapshot", Secret))
        {
            put.Content = new StringContent(
                """[{"id":"c2","destinations":[{"id":"d2","address":"10.0.0.2","port":8080}]}]""",
                Encoding.UTF8, "application/json");
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);
        }

        using (var rejected = Request(HttpMethod.Put, server.Prefix + "v1/snapshot", Secret))
        {
            rejected.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, (await http.SendAsync(rejected)).StatusCode);
        }

        using (var purge = Request(HttpMethod.Post, server.Prefix + "v1/cache/purge?prefix=a%2Fb", Secret))
        {
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(purge)).StatusCode);
        }

        var entries = audit.Recent().Reverse().ToList();
        Assert.AreEqual(4, entries.Count);
        Assert.AreEqual((AuditActions.LoginFailed, AuditResults.Denied, "/v1/snapshot"), (entries[0].Action, entries[0].Result, entries[0].Target));
        Assert.AreEqual((AuditActions.SnapshotPut, AuditResults.Ok), (entries[1].Action, entries[1].Result));
        Assert.AreEqual((AuditActions.SnapshotPut, AuditResults.Rejected), (entries[2].Action, entries[2].Result));
        Assert.AreEqual((AuditActions.CachePurge, AuditResults.Ok, "a/b"), (entries[3].Action, entries[3].Result, entries[3].Target));
        Assert.AreEqual("control-plane:anonymous", entries[0].Actor);
        Assert.AreEqual("control-plane:shared-secret", entries[1].Actor);
        Assert.AreEqual("127.0.0.1", entries[1].Remote);
        Assert.AreEqual(clock.GetUtcNow(), entries[1].Time);

        audit.Dispose();
        var file = string.Join("\n", ReadLines(auditPath));
        Assert.AreEqual(4, ReadLines(auditPath).Count);
        Assert.IsFalse(file.Contains(Secret, StringComparison.Ordinal), "the shared secret is never written");
        Assert.IsFalse(file.Contains(wrongSecret, StringComparison.Ordinal), "a presented guess is never written");
        Assert.IsFalse(file.Contains("hardening-test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ControlPlane_AnswersTooManyFailures_With429AndRetryAfter_ThenReleases()
    {
        var clock = new ManualTimeProvider();
        using var audit = new AuditLog(timeProvider: clock);
        var security = new ControlPlaneSecurity(audit, new AuthThrottle(timeProvider: clock));
        using var server = new ControlPlaneServer(
            new ClusterManager(), "127.0.0.1", 0, Secret, new List<RouteConfig>(), null, null, security);
        server.Start();
        using var http = new HttpClient();

        for (var i = 0; i < 5; i++)
        {
            using var bad = Request(HttpMethod.Get, server.Prefix + "v1/snapshot", "wrong-" + i);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);
        }

        using (var sixth = Request(HttpMethod.Get, server.Prefix + "v1/snapshot", "wrong-5"))
        {
            var resp = await http.SendAsync(sixth);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, resp.StatusCode);
            Assert.AreEqual("60", resp.Headers.GetValues("Retry-After").Single());
        }

        using (var correct = Request(HttpMethod.Get, server.Prefix + "v1/snapshot", Secret))
        {
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.SendAsync(correct)).StatusCode,
                "a locked-out remote stays locked out even with the right secret");
        }

        var throttledEntries = audit.Recent().Count(e => e.Result == AuditResults.Throttled);
        Assert.AreEqual(1, throttledEntries, "a lockout is audited once, not once per refused request");
        Assert.AreEqual(5, audit.Recent().Count(e => e.Result == AuditResults.Denied));

        clock.Advance(TimeSpan.FromSeconds(61));
        using var ok = Request(HttpMethod.Get, server.Prefix + "v1/snapshot", Secret);
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);
    }

    [TestMethod]
    public async Task ControlPlane_RejectsAMissingHeader_AndCountsItAsAFailure()
    {
        using var server = new ControlPlaneServer(new ClusterManager(), "127.0.0.1", 0, Secret);
        server.Start();
        using var http = new HttpClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(server.Prefix + "v1/snapshot")).StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.GetAsync(server.Prefix + "v1/snapshot")).StatusCode);
    }

    [TestMethod]
    public async Task ControlPlane_ServesChunkedBodiesAndRejectsOversizedOnes()
    {
        var manager = new ClusterManager();
        using var server = new ControlPlaneServer(manager, "127.0.0.1", 0, Secret);
        server.Start();

        var body = """[{"id":"chunked","destinations":[{"id":"cd","address":"10.0.0.3","port":81}]}]""";
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            var half = body.Length / 2;
            var request =
                $"PUT /v1/snapshot HTTP/1.1\r\nHost: x\r\n{ControlPlaneServer.SharedSecretHeader}: {Secret}\r\n" +
                "Transfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n" +
                $"{half:X}\r\n{body[..half]}\r\n{body.Length - half:X}\r\n{body[half..]}\r\n0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            var reply = await new StreamReader(stream).ReadToEndAsync();
            StringAssert.StartsWith(reply, "HTTP/1.1 200");
        }

        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("chunked"));

        using var big = new TcpClient();
        await big.ConnectAsync(IPAddress.Loopback, server.Port);
        var bigStream = big.GetStream();
        await bigStream.WriteAsync(Encoding.ASCII.GetBytes(
            $"PUT /v1/snapshot HTTP/1.1\r\nHost: x\r\n{ControlPlaneServer.SharedSecretHeader}: {Secret}\r\nContent-Length: 99999999\r\n\r\n"));
        StringAssert.StartsWith(await new StreamReader(bigStream).ReadToEndAsync(), "HTTP/1.1 413");
    }

    // ---- dashboard --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Dashboard_AuditsDrainHealthyAndLogin_ShowsThemOnThePage_AndHidesNoSecret()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d<1>", Address = "127.0.0.1", Port = 80 }],
            },
        ]);
        var auditPath = Path.Combine(_dir, "dash-audit.ndjson");
        using var audit = new AuditLog(auditPath);
        using var control = new ControlPlaneServer(
            manager, "127.0.0.1", 0, Secret, null, null, null, new ControlPlaneSecurity(audit));
        control.Start();
        using var dash = new DashboardHost(control, new DrainOperations(manager), new PrometheusMetricsExporter(manager, null), manager);
        dash.Start();
        using var http = new HttpClient();

        using (var page = Request(HttpMethod.Get, dash.Prefix!, Secret))
        {
            var html = await (await http.SendAsync(page)).Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Titanium Plus");
        }

        using (var drain = Request(HttpMethod.Post, dash.Prefix + "drain/" + Uri.EscapeDataString("d<1>"), Secret))
        {
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(drain)).StatusCode);
        }

        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d<1>"));

        using (var healthy = Request(HttpMethod.Post, dash.Prefix + "healthy/" + Uri.EscapeDataString("d<1>"), Secret))
        {
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(healthy)).StatusCode);
        }

        using (var blank = Request(HttpMethod.Post, dash.Prefix + "drain/", Secret))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await http.SendAsync(blank)).StatusCode);
        }

        using (var unauth = new HttpRequestMessage(HttpMethod.Post, dash.Prefix + "drain/d1"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(unauth)).StatusCode);
        }

        using (var api = Request(HttpMethod.Get, dash.Prefix + "api/audit", Secret))
        {
            var json = await (await http.SendAsync(api)).Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.AreEqual(5, rows.Count);
            Assert.AreEqual("login-failed", rows[0].GetProperty("action").GetString());
            Assert.AreEqual("drain", rows[1].GetProperty("action").GetString());
            Assert.AreEqual("rejected", rows[1].GetProperty("result").GetString());
            Assert.AreEqual("healthy", rows[2].GetProperty("action").GetString());
            Assert.AreEqual("d<1>", rows[2].GetProperty("target").GetString());
            Assert.AreEqual("drain", rows[3].GetProperty("action").GetString());
            Assert.AreEqual("dashboard:shared-secret", rows[3].GetProperty("actor").GetString());
            Assert.AreEqual("login", rows[4].GetProperty("action").GetString());
            Assert.AreEqual("/", rows[4].GetProperty("target").GetString());
            Assert.IsFalse(json.Contains(Secret, StringComparison.Ordinal));
        }

        using (var page = Request(HttpMethod.Get, dash.Prefix + "audit", Secret))
        {
            var html = await (await http.SendAsync(page)).Content.ReadAsStringAsync();
            StringAssert.Contains(html, "d&lt;1&gt;", "audit targets are HTML-encoded");
            Assert.IsFalse(html.Contains(Secret, StringComparison.Ordinal));
        }

        audit.Dispose();
        Assert.IsFalse(File.ReadAllText(auditPath).Contains(Secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Dashboard_SharesTheControlPlaneThrottle()
    {
        using var control = new ControlPlaneServer(new ClusterManager(), "127.0.0.1", 0, Secret);
        control.Start();
        using var dash = new DashboardHost(
            control, new DrainOperations(null), new PrometheusMetricsExporter(null, null), null);
        dash.Start();
        using var http = new HttpClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(dash.Prefix + "api/snapshot")).StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.GetAsync(control.Prefix + "v1/snapshot")).StatusCode);
    }

    // ---- transport -----------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("localhost", true)]
    [DataRow("::1", true)]
    [DataRow("[::1]", true)]
    [DataRow("127.5.5.5", true)]
    [DataRow("0.0.0.0", false)]
    [DataRow("::", false)]
    [DataRow("10.0.0.5", false)]
    [DataRow("proxy.example.com", false)]
    public void IsLoopbackHost_ClassifiesHosts(string host, bool loopback)
    {
        Assert.AreEqual(loopback, ControlPlaneServer.IsLoopbackHost(host));
    }

    [TestMethod]
    public void ValidateTransport_RejectsNonLoopbackWithoutTls_UnlessExplicitlyAllowed()
    {
        var tls = new ControlPlaneTlsOptions("cert.pem", "key.pem");

        ControlPlaneServer.ValidateTransport("127.0.0.1", null);
        ControlPlaneServer.ValidateTransport("0.0.0.0", tls);
        ControlPlaneServer.ValidateTransport("0.0.0.0", null, allowInsecureNonLoopback: true);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ControlPlaneServer.ValidateTransport("0.0.0.0", null));
        StringAssert.Contains(ex.Message, "controlPlane.tls");
        StringAssert.Contains(ex.Message, "allowInsecureNonLoopback");
        Assert.ThrowsExactly<InvalidOperationException>(() => ControlPlaneServer.ValidateTransport("10.1.2.3", null, false));
    }

    [TestMethod]
    public void Start_RefusesANonLoopbackHostWithoutTls_BeforeBinding()
    {
        using var server = new ControlPlaneServer(new ClusterManager(), "0.0.0.0", 0, Secret);

        Assert.ThrowsExactly<InvalidOperationException>(server.Start);
    }

    [TestMethod]
    public void Module_RefusesANonLoopbackHostWithoutTls()
    {
        var options = new Dictionary<string, string>
        {
            ["controlPlane.host"] = "0.0.0.0",
            ["controlPlane.port"] = "0",
            ["controlPlane.sharedSecret"] = Secret,
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new TitaniumPlusModule().Apply(new PlusActivationContext { ProxyServer = new object(), Options = options }));
        StringAssert.Contains(ex.Message, "not loopback");
    }

    [TestMethod]
    public void Module_RefusesHalfAConfiguredCertificate()
    {
        var options = new Dictionary<string, string>
        {
            ["controlPlane.port"] = "0",
            ["controlPlane.sharedSecret"] = Secret,
            ["controlPlane.tls.certificatePath"] = "only-a-cert.pem",
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new TitaniumPlusModule().Apply(new PlusActivationContext { ProxyServer = new object(), Options = options }));
        StringAssert.Contains(ex.Message, "certificatePath and keyPath");
    }

    [TestMethod]
    public async Task ControlPlane_ServesHttpsFromAPemCertificate_OnControlPlaneAndDashboard()
    {
        var (certPath, keyPath, thumbprint) = WriteSelfSignedPem();
        var manager = new ClusterManager();
        using var control = new ControlPlaneServer(
            manager, "127.0.0.1", 0, Secret, null, null, null, null,
            new ControlPlaneTlsOptions(certPath, keyPath));
        control.Start();
        Assert.IsTrue(control.IsTls);
        StringAssert.StartsWith(control.Prefix, "https://127.0.0.1:");

        using var dash = new DashboardHost(control, new DrainOperations(manager), new PrometheusMetricsExporter(manager, null), manager);
        dash.Start();
        StringAssert.StartsWith(dash.Prefix!, "https://127.0.0.1:");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == thumbprint,
        };
        using var https = new HttpClient(handler);

        using (var get = Request(HttpMethod.Get, control.Prefix + "v1/snapshot", Secret))
        {
            Assert.AreEqual(HttpStatusCode.OK, (await https.SendAsync(get)).StatusCode);
        }

        using (var put = Request(HttpMethod.Put, control.Prefix + "v1/snapshot", Secret))
        {
            put.Content = new StringContent(
                """[{"id":"tls-c","destinations":[{"id":"tls-d","address":"10.0.0.7","port":80}]}]""",
                Encoding.UTF8, "application/json");
            Assert.AreEqual(HttpStatusCode.OK, (await https.SendAsync(put)).StatusCode);
        }

        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("tls-c"));

        using (var page = Request(HttpMethod.Get, dash.Prefix!, Secret))
        {
            var response = await https.SendAsync(page);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Titanium Plus");
        }

        // The wrong trust anchor fails the handshake; plain HTTP to a TLS port gets no answer.
        using var strict = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync(control.Prefix + "v1/snapshot"));
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(
            () => plain.GetAsync($"http://127.0.0.1:{control.Port}/v1/snapshot"));
    }

    [TestMethod]
    public void Start_ReportsAMissingOrMismatchedCertificate()
    {
        var (certPath, _, _) = WriteSelfSignedPem();
        var missing = new ControlPlaneServer(
            new ClusterManager(), "127.0.0.1", 0, Secret, null, null, null, null,
            new ControlPlaneTlsOptions(Path.Combine(_dir, "nope.pem"), Path.Combine(_dir, "nope.key")));
        var mismatched = new ControlPlaneServer(
            new ClusterManager(), "127.0.0.1", 0, Secret, null, null, null, null,
            new ControlPlaneTlsOptions(certPath, WriteSelfSignedPem().KeyPath));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(missing.Start);
        StringAssert.Contains(ex.Message, "could not be loaded");
        Assert.ThrowsExactly<InvalidOperationException>(mismatched.Start);
    }

    [TestMethod]
    public async Task Module_StartsOverTls_FromOptions_AndWritesTheAuditFile()
    {
        var (certPath, keyPath, thumbprint) = WriteSelfSignedPem();
        var auditPath = Path.Combine(_dir, "module-audit.ndjson");
        var port = FreePort();
        var options = new Dictionary<string, string>
        {
            ["controlPlane.host"] = "127.0.0.1",
            ["controlPlane.port"] = port.ToString(),
            ["controlPlane.sharedSecret"] = Secret,
            ["controlPlane.tls.certificatePath"] = certPath,
            ["controlPlane.tls.keyPath"] = keyPath,
            ["audit.path"] = auditPath,
        };
        var logger = new ListLogger();

        new TitaniumPlusModule().Apply(new PlusActivationContext
        {
            ProxyServer = new object(),
            ClusterManager = new ClusterManager(),
            Routes = new List<RouteConfig>(),
            Options = options,
            Logger = logger,
        });

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == thumbprint,
        };
        using var https = new HttpClient(handler);
        using var bad = Request(HttpMethod.Get, $"https://127.0.0.1:{port}/v1/snapshot", "wrong");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await https.SendAsync(bad)).StatusCode);

        var line = await WaitForLineAsync(auditPath);
        StringAssert.Contains(line, "login-failed");
        StringAssert.Contains(logger.Text, auditPath);
        Assert.IsFalse(logger.Text.Contains(Secret, StringComparison.Ordinal));
    }

    // ---- startup warnings and secret-free logs ---------------------------------------------------------------------------

    [TestMethod]
    public void AccessSecurity_WarnsAboutPlaintextUsers_ByNameOnly()
    {
        var middleware = new List<IProxyMiddleware>();
        var logger = new ListLogger();
        var hash = "{bcrypt}" + BCrypt.Net.BCrypt.HashPassword("hash-pass", workFactor: 4);
        var options = new Dictionary<string, string>
        {
            ["security.basicUsers"] = $"legacy:hunter2-plain,modern:{hash}",
        };

        AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = middleware, Options = options, Logger = logger },
            options);

        Assert.AreEqual(1, middleware.Count);
        var warning = logger.Messages.Single(m => m.Level == LogLevel.Warning).Text;
        StringAssert.Contains(warning, "plaintext");
        StringAssert.Contains(warning, "legacy");
        Assert.IsFalse(warning.Contains("modern", StringComparison.Ordinal), "a hashed user is not flagged");
        Assert.IsFalse(logger.Text.Contains("hunter2-plain", StringComparison.Ordinal));
        Assert.IsFalse(logger.Text.Contains("hash-pass", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AccessSecurity_DoesNotWarn_WhenEveryUserIsHashed()
    {
        var middleware = new List<IProxyMiddleware>();
        var logger = new ListLogger();
        var options = new Dictionary<string, string>
        {
            ["security.basicUsers"] = "modern:" + Pbkdf2PasswordHash.Format("pw", RandomNumberGenerator.GetBytes(16), 1000, 32),
        };

        AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = middleware, Options = options, Logger = logger },
            options);

        Assert.AreEqual(1, middleware.Count);
        Assert.AreEqual(0, logger.Messages.Count(m => m.Level == LogLevel.Warning));
    }

    [TestMethod]
    public void AccessSecurity_RefusesToStart_OnAMalformedHash()
    {
        var options = new Dictionary<string, string> { ["security.basicUsers"] = "u:{bcrypt}oops" };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = [], Options = options },
            options));
        StringAssert.Contains(ex.Message, "'u'");
    }

    [TestMethod]
    public void SharedStateStore_NeverLogsTheRedisPassword()
    {
        var logger = new ListLogger();
        var options = new Dictionary<string, string>
        {
            ["state.redis"] = "127.0.0.1:1,password=redis-hunter-secret,abortConnect=false,connectTimeout=200",
        };

        SharedStateStore.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = [], Options = options, Logger = logger },
            options);

        Assert.IsTrue(logger.Messages.Count > 0);
        Assert.IsFalse(logger.Text.Contains("redis-hunter-secret", StringComparison.Ordinal), logger.Text);
        StringAssert.Contains(logger.Text, "127.0.0.1:1");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string url, string secret)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ControlPlaneServer.SharedSecretHeader, secret);
        return request;
    }

    private static List<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static async Task<string> WaitForLineAsync(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(path) && ReadLines(path) is { Count: > 0 } lines)
            {
                return lines[0];
            }

            await Task.Delay(50);
        }

        Assert.Fail("the audit file never received a line");
        return "";
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private (string CertPath, string KeyPath, string Thumbprint) WriteSelfSignedPem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var id = Guid.NewGuid().ToString("N");
        var certPath = Path.Combine(_dir, id + ".crt");
        var keyPath = Path.Combine(_dir, id + ".key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath, cert.Thumbprint);
    }

    private static async Task<bool> Allows(ApiKeyBasicAuthMiddleware mw, string key)
    {
        var context = new ProxyMiddlewareContext
        {
            Session = new object(),
            Request = new MiddlewareRequestView
            {
                Method = "GET",
                Path = "/",
                Host = "example",
                GetHeaderValues = name => name == "X-Api-Key" ? new List<string> { key } : null,
            },
        };
        var next = false;
        await mw.InvokeAsync(context, (_, _) =>
        {
            next = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        return next && !context.IsHandled;
    }

    private static async Task<bool> BasicAllows(ApiKeyBasicAuthMiddleware mw, string user, string password) =>
        !(await Invoke(mw, user, password, null)).IsHandled;

    private static async Task<ProxyMiddlewareContext> Invoke(
        ApiKeyBasicAuthMiddleware mw, string user, string password, IPEndPoint? remote)
    {
        var context = new ProxyMiddlewareContext
        {
            Session = new object(),
            ClientRemoteEndPoint = remote,
            Request = new MiddlewareRequestView
            {
                Method = "GET",
                Path = "/",
                Host = "example",
                Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")),
            },
        };
        await mw.InvokeAsync(context, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public string Text => string.Join("\n", Messages.Select(m => m.Text));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class CountingCache : IHttpResponseCache
    {
        public int Count => 0;

        public bool TryGet(string cacheKey, out CachedHttpResponse? response)
        {
            response = null;
            return false;
        }

        public void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl)
        {
        }

        public int Purge(string? pathPrefix = null) => 1;
    }
}
