using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Caching;

namespace Titanium.Web.Proxy.UnitTests.Caching;

/// <summary>
/// Runs <c>rfc9111-vectors.json</c> against the pure cache policy: whether a response is stored, for how long, and
/// what the cache decides for a later request at a given age. Adding a case to the file needs no code.
/// </summary>
[TestClass]
public class Rfc9111VectorTests
{
    private static readonly DateTimeOffset TimeZero = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> VectorNames()
    {
        foreach (var vector in LoadVectors())
        {
            yield return [vector.GetProperty("name").GetString()!];
        }
    }

    [TestMethod]
    public void VectorFile_HasCoverage()
    {
        var vectors = LoadVectors();
        Assert.IsTrue(vectors.Count >= 40, $"expected a substantial vector table, found {vectors.Count}");
        Assert.AreEqual(vectors.Count, vectors.Select(v => v.GetProperty("name").GetString()).Distinct().Count(),
            "vector names must be unique");
    }

    [TestMethod]
    [DynamicData(nameof(VectorNames))]
    public void Vector(string name)
    {
        var vector = LoadVectors().Single(v => v.GetProperty("name").GetString() == name);
        var status = vector.GetProperty("status").GetInt32();
        var headers = vector.GetProperty("headers").EnumerateArray().Select(e => ParseHeader(e.GetString()!)).ToList();

        var options = new ResponseCacheOptions();
        if (vector.TryGetProperty("options", out var o))
        {
            options = new ResponseCacheOptions
            {
                HeuristicFreshness = o.TryGetProperty("heuristic", out var h) && h.GetBoolean(),
                DefaultTtl = o.TryGetProperty("defaultTtlSeconds", out var t) ? TimeSpan.FromSeconds(t.GetInt64()) : null,
            };
        }

        var stored = HttpCachePolicy.TryPlanStore(status, headers, TimeZero, options, out var plan, out var refusal);
        Assert.AreEqual(vector.GetProperty("store").GetBoolean(), stored, $"{name}: store (refusal '{refusal}')");

        if (!stored)
        {
            if (vector.TryGetProperty("refusal", out var expectedRefusal))
            {
                Assert.AreEqual(expectedRefusal.GetString(), refusal, $"{name}: refusal reason");
            }

            return;
        }

        Assert.IsNotNull(plan);
        if (vector.TryGetProperty("lifetime", out var lifetime))
        {
            Assert.AreEqual(lifetime.GetInt64(), plan.FreshnessLifetimeSeconds, $"{name}: lifetime");
        }

        if (vector.TryGetProperty("vary", out var vary))
        {
            CollectionAssert.AreEqual(vary.EnumerateArray().Select(v => v.GetString()).ToList(), plan.VaryNames.ToList(),
                $"{name}: vary names");
        }

        if (!vector.TryGetProperty("checks", out var checks))
        {
            return;
        }

        var entry = HttpCachePolicy.BuildEntry(plan, [1, 2, 3]);
        foreach (var check in checks.EnumerateArray())
        {
            var at = TimeZero.AddSeconds(check.GetProperty("at").GetInt64());
            var request = check.TryGetProperty("request", out var r)
                ? HttpCachePolicy.RequestDirectives.From(HeaderLookup(r.EnumerateArray().Select(e => ParseHeader(e.GetString()!))))
                : HttpCachePolicy.RequestDirectives.None;
            var age = HttpCachePolicy.Evaluate(entry, at);
            var label = $"{name}: at {check.GetProperty("at").GetInt64()}s";

            if (check.TryGetProperty("decision", out var decision))
            {
                Assert.AreEqual(decision.GetString(), HttpCachePolicy.Decide(entry, age, request).ToString(), label);
            }

            if (check.TryGetProperty("sie", out var sie))
            {
                Assert.AreEqual(sie.GetBoolean(), HttpCachePolicy.MayServeStaleOnError(entry, age, request),
                    label + " stale-if-error");
            }
        }
    }

    [TestMethod]
    public void BuildStoredHeaders_DropsHopByHopAndNormalizesFraming()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Connection", "keep-alive"),
            new("Transfer-Encoding", "chunked"),
            new("Set-Cookie", "a=b"),
            new("Age", "5"),
            new("Content-Length", "999"),
            new("Content-Type", "text/plain"),
            new("Cache-Control", "max-age=60"),
        };
        var stored = HttpCachePolicy.BuildStoredHeaders(headers, 200, 3, TimeZero, hasDate: false);

        Assert.IsFalse(stored.Exists(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(stored.Exists(h => h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(stored.Exists(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(stored.Exists(h => h.Key.Equals("Age", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(1, stored.Count(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("3", stored.Single(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.IsTrue(stored.Exists(h => h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void MergeNotModified_UpdatesFreshnessAndKeepsRepresentation()
    {
        Assert.IsTrue(HttpCachePolicy.TryPlanStore(200,
            [
                new("Date", "Mon, 01 Jan 2024 00:00:00 GMT"),
                new("Cache-Control", "max-age=10"),
                new("ETag", "\"v1\""),
                new("Content-Type", "text/plain"),
                new("Content-Encoding", "gzip"),
            ], TimeZero, new ResponseCacheOptions(), out var plan, out _));
        var entry = HttpCachePolicy.BuildEntry(plan!, [9, 9]);

        var merged = HttpCachePolicy.MergeNotModified(entry,
        [
            new("Cache-Control", "max-age=300"),
            new("Content-Encoding", "identity"),
            new("Content-Length", "0"),
            new("Content-Type", "text/html"),
            new("X-Fresh", "yes"),
            new("Connection", "close"),
        ]);

        Assert.AreEqual("max-age=300", HttpCachePolicy.First(merged, "Cache-Control"));
        Assert.AreEqual("yes", HttpCachePolicy.First(merged, "X-Fresh"));
        Assert.AreEqual("gzip", HttpCachePolicy.First(merged, "Content-Encoding"), "the stored encoding must survive");
        Assert.AreEqual("text/plain", HttpCachePolicy.First(merged, "Content-Type"), "the stored type must survive");
        Assert.AreEqual("2", HttpCachePolicy.First(merged, "Content-Length"), "the stored length must survive");
        Assert.IsNull(HttpCachePolicy.First(merged, "Connection"));
        Assert.AreEqual("\"v1\"", HttpCachePolicy.First(merged, "ETag"));
    }

    [TestMethod]
    [DataRow("gzip, br", "br,gzip")]
    [DataRow("GZIP;q=1.0, br;q=0.5", "br,gzip")]
    [DataRow("gzip;q=0, br", "br")]
    [DataRow("", "")]
    public void NormalizeAcceptEncoding_IsCanonical(string raw, string expected)
    {
        var normalized = HttpCachePolicy.NormalizeAcceptEncoding(raw);
        Assert.AreEqual(HttpCachePolicy.NormalizeAcceptEncoding(expected), normalized);
    }

    [TestMethod]
    public void BuildSecondaryKey_OversizedKeyPart_Bypasses()
    {
        Assert.IsNull(HttpCachePolicy.BuildSecondaryKey("x-" + new string('a', 500), null, null),
            "an oversized Accept-Encoding must bypass, not mint a huge key");
        Assert.IsNull(HttpCachePolicy.BuildSecondaryKey("gzip", ["accept-language"], _ => [new string('z', 500)]),
            "an oversized Vary value must bypass");
        Assert.IsNotNull(HttpCachePolicy.BuildSecondaryKey("gzip, br", ["accept-language"], _ => ["en"]));
    }

    [TestMethod]
    [DataRow("a\u0001b")]
    [DataRow("a\r\nb")]
    [DataRow("a\0b")]
    public void IsKeySafe_RejectsControlCharacters(string text)
    {
        Assert.IsFalse(HttpCachePolicy.IsKeySafe(text));
    }

    [TestMethod]
    public void BuildSecondaryKey_UnsafeVaryValue_ReturnsNull()
    {
        string[] vary = ["accept-language"];
        Assert.IsNull(HttpCachePolicy.BuildSecondaryKey("gzip", vary, name =>
            name == "accept-language" ? ["en\u0001ae=br"] : null));
        Assert.IsNotNull(HttpCachePolicy.BuildSecondaryKey("gzip", vary, name => name == "accept-language" ? ["en"] : null));
    }

    [TestMethod]
    public void BuildSecondaryKey_UnnamedHeadersNeverInfluenceTheKey()
    {
        string[] vary = ["accept-language"];
        var plain = HttpCachePolicy.BuildSecondaryKey("gzip", vary, name => name == "accept-language" ? ["en"] : null);
        var noisy = HttpCachePolicy.BuildSecondaryKey("gzip", vary, name => name switch
        {
            "accept-language" => ["en"],
            "x-evil" => ["1"],
            "user-agent" => ["curl"],
            _ => null,
        });
        Assert.AreEqual(plain, noisy);
    }

    [TestMethod]
    public void ParseCacheControl_HandlesQuotedValuesAndJunk()
    {
        var d = HttpCachePolicy.ParseCacheControl(
            new[] { "max-age=\"60\", no-transform, foo=bar,,  ,stale-while-revalidate=5" });
        Assert.AreEqual(60, d.MaxAge);
        Assert.AreEqual(5, d.StaleWhileRevalidate);
        Assert.IsFalse(d.NoStore);

        var bad = HttpCachePolicy.ParseCacheControl(new[] { "max-age=-5" });
        Assert.IsTrue(bad.MalformedFreshness);
    }

    [TestMethod]
    [DataRow("If-None-Match", "\"v1\"", true)]
    [DataRow("If-None-Match", "W/\"v1\"", true)]
    [DataRow("If-None-Match", "\"other\"", false)]
    [DataRow("If-None-Match", "*", true)]
    [DataRow("If-Modified-Since", "Mon, 01 Jan 2024 00:00:00 GMT", true)]
    [DataRow("If-Modified-Since", "Sun, 31 Dec 2023 00:00:00 GMT", false)]
    public void EvaluateClientConditional_AnswersNotModified(string header, string value, bool notModified)
    {
        Assert.IsTrue(HttpCachePolicy.TryPlanStore(200,
            [
                new("Date", "Mon, 01 Jan 2024 00:00:00 GMT"),
                new("Cache-Control", "max-age=60"),
                new("ETag", "\"v1\""),
                new("Last-Modified", "Mon, 01 Jan 2024 00:00:00 GMT"),
            ], TimeZero, new ResponseCacheOptions(), out var plan, out _));
        var entry = HttpCachePolicy.BuildEntry(plan!, [1]);
        var result = HttpCachePolicy.EvaluateClientConditional(entry, name =>
            name.Equals(header, StringComparison.OrdinalIgnoreCase) ? [value] : null);
        Assert.AreEqual(notModified, result == HttpCachePolicy.ClientConditional.NotModified);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static List<JsonElement> LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Caching", "rfc9111-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static KeyValuePair<string, string> ParseHeader(string line)
    {
        var colon = line.IndexOf(':');
        return new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim());
    }

    private static Func<string, IReadOnlyList<string>?> HeaderLookup(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var list = headers.ToList();
        return name =>
        {
            var values = list.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
            return values.Count == 0 ? null : values;
        };
    }
}
