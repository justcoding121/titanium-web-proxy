using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     <c>website/docs/.capabilities.yml</c> is the data behind the capability coverage tables on the website and the
///     wiki. These tests keep it honest: every config key exists in <c>twp.schema.json</c> (or is explicitly marked
///     <c>n/a</c>), every link target exists, and both pages carry exactly the rows the data file lists.
/// </summary>
[TestClass]
public class CapabilityCoverageTests
{
    private const string NotApplicable = "n/a";
    private const string WikiSiteRoot = "https://titaniumproxy.com";

    private static readonly string[] Statuses = ["supported", "partial", "not supported"];

    private static readonly string[] RequiredAreas =
    [
        "Routing", "Load balancing", "Health checks", "Retries and budgets", "Timeouts", "Rate limiting",
        "Auth methods", "mTLS", "TLS policy", "Caching", "Compression", "WAF", "Observability", "Kubernetes",
        "Protocols", "L4", "Extensibility",
    ];

    // One entry per capability that is deliberately not offered. The data file must list each, with a reason.
    private static readonly string[] RequiredNotSupported =
    [
        "WebSocket over HTTP/3",
        "h2c Upgrade",
        "Decrypting QUIC inside CONNECT-UDP",
        "0-RTT",
        "TLSRoute",
        "xDS",
        "Lua",
        "WASM",
        "route editing",
        "Multi-user dashboard accounts",
        "SSO beyond",
        "Hot binary upgrade",
        "Geo-IP",
    ];

    private static string DataPath => Path.Combine(DocsRepository.WebsiteDocs, ".capabilities.yml");

    private static CapabilityFile Load()
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<CapabilityFile>(File.ReadAllText(DataPath));
    }

    [TestMethod]
    public void DataFile_CoversEveryAreaWithValidStatuses()
    {
        var file = Load();

        var areas = file.Rows.Select(r => r.Area).Distinct(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(RequiredAreas, areas, "Areas must appear once each, in the order the page shows them.");

        foreach (var row in file.Rows)
        {
            var label = $"{row.Area}: {row.Capability}";
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Capability), $"{label}: capability is empty.");
            Assert.IsTrue(Statuses.Contains(row.Status), $"{label}: status '{row.Status}' is not one of {string.Join(", ", Statuses)}.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Docs), $"{label}: docs link is empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.DocsLabel), $"{label}: docsLabel is empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), $"{label}: note is empty.");
            Assert.IsTrue(row.Schema is null or NotApplicable, $"{label}: schema must be omitted or '{NotApplicable}'.");

            if (row.Status == "not supported")
            {
                Assert.AreEqual(0, row.Keys.Count, $"{label}: a not-supported row has no config key.");
                Assert.AreEqual(NotApplicable, row.Schema, $"{label}: a not-supported row is marked {NotApplicable}.");
            }
            else if (row.Schema is null)
            {
                Assert.IsTrue(row.Keys.Count > 0, $"{label}: needs a config key, or schema: {NotApplicable}.");
            }
            else
            {
                Assert.IsTrue(row.Keys.Count > 0, $"{label}: a {NotApplicable} row still names what to set.");
            }
        }
    }

    [TestMethod]
    public void DataFile_ListsEveryUnsupportedCapabilityWithAReason()
    {
        var file = Load();
        var unsupported = file.Rows.Where(r => r.Status == "not supported").ToList();

        foreach (var topic in RequiredNotSupported)
        {
            var row = unsupported.FirstOrDefault(r => r.Capability.Contains(topic, StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(row, $"No not-supported row mentions '{topic}'.");
            Assert.IsTrue(row.Note.Length >= 20, $"'{topic}' needs a reason in its note.");
        }

        Assert.AreEqual(
            RequiredNotSupported.Length,
            unsupported.Count,
            "Each not-supported row should answer one entry of RequiredNotSupported. Update both when the list changes.");
    }

    [TestMethod]
    public void ConfigKeys_ExistInSchemaOrAreMarkedNotApplicable()
    {
        var file = Load();
        using var schema = JsonDocument.Parse(File.ReadAllText(DocsRepository.SchemaPath));
        var failures = new List<string>();

        foreach (var row in file.Rows.Where(r => r.Schema is null))
        {
            foreach (var key in row.Keys)
            {
                if (!SchemaHasKey(schema.RootElement, key, out var why))
                {
                    failures.Add($"{row.Area}: {row.Capability}: '{key}' {why}");
                }
            }
        }

        Assert.AreEqual(0, failures.Count, "Keys missing from twp.schema.json:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void SchemaKeyResolver_AcceptsRealKeysAndRejectsTypos()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(DocsRepository.SchemaPath));

        Assert.IsTrue(SchemaHasKey(schema.RootElement, "routes[].retry.attempts", out _));
        Assert.IsTrue(SchemaHasKey(schema.RootElement, "server.responseCompression", out _));
        Assert.IsTrue(SchemaHasKey(schema.RootElement, "clusters[].healthCheck.passive.maxEjectionPercent", out _));
        Assert.IsFalse(SchemaHasKey(schema.RootElement, "routes[].retry.attemps", out _));
        Assert.IsFalse(SchemaHasKey(schema.RootElement, "routes.retry", out _), "routes is an array and needs [].");
        Assert.IsFalse(SchemaHasKey(schema.RootElement, "server.nope", out _));
    }

    [TestMethod]
    public void DocsLinks_PointAtExistingPagesAndHeadings()
    {
        var file = Load();
        var failures = new List<string>();

        foreach (var row in file.Rows)
        {
            var target = row.Docs;
            if (!target.StartsWith("/docs/", StringComparison.Ordinal))
            {
                failures.Add($"{row.Capability}: docs '{target}' must be a /docs/ path.");
                continue;
            }

            var hash = target.IndexOf('#', StringComparison.Ordinal);
            var page = hash < 0 ? target : target[..hash];
            var anchor = hash < 0 ? null : target[(hash + 1)..];
            var path = Path.Combine(DocsRepository.WebsiteDocs, page["/docs/".Length..] + ".md");

            if (!File.Exists(path))
            {
                failures.Add($"{row.Capability}: {page} has no file {path}.");
                continue;
            }

            if (anchor is not null && !DocsRepository.WebsiteHeadingIds(path).Contains(anchor))
            {
                failures.Add($"{row.Capability}: {page} has no heading with id '{anchor}'.");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    [TestMethod]
    public void WebsitePage_ListsEveryRowFromTheDataFile()
    {
        AssertPageHasRows(
            Path.Combine(DocsRepository.WebsiteDocs, "capabilities.md"),
            docsPrefix: string.Empty);
    }

    [TestMethod]
    public void WikiPage_ListsEveryRowFromTheDataFile()
    {
        AssertPageHasRows(
            Path.Combine(DocsRepository.Wiki, "Capabilities.md"),
            docsPrefix: WikiSiteRoot);
    }

    [TestMethod]
    public void WikiPage_DoesNotRepeatItsFileNameAsAnH1()
    {
        var first = DocsRepository.FirstNonBlankLine(Path.Combine(DocsRepository.Wiki, "Capabilities.md"));
        Assert.IsFalse(first.StartsWith("# ", StringComparison.Ordinal), "The wiki shows the file name as the title; start with the intro.");
    }

    [TestMethod]
    public void Sidebar_LinksToTheCapabilitiesPage()
    {
        var config = File.ReadAllText(Path.Combine(DocsRepository.Root, "website", ".vitepress", "config.mts"));
        StringAssert.Contains(config, "link: '/docs/capabilities'");
    }

    private static void AssertPageHasRows(string path, string docsPrefix)
    {
        var file = Load();
        var lines = File.ReadAllLines(path);
        var lineSet = new HashSet<string>(lines, StringComparer.Ordinal);
        var failures = new List<string>();

        var headings = DocsRepository.Headings(lines).ToList();
        foreach (var area in RequiredAreas)
        {
            if (!headings.Contains(area, StringComparer.Ordinal))
            {
                failures.Add($"Missing heading '## {area}'.");
            }
        }

        foreach (var row in file.Rows)
        {
            var expected = FormatRow(row, docsPrefix);
            if (!lineSet.Contains(expected))
            {
                failures.Add($"Missing or different row: {expected}");
            }
        }

        var tableRows = lines.Count(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Capability", StringComparison.Ordinal));
        if (tableRows != file.Rows.Count)
        {
            failures.Add($"The page has {tableRows} table rows; the data file has {file.Rows.Count}.");
        }

        Assert.AreEqual(0, failures.Count, path + ":\n" + string.Join("\n", failures));
    }

    /// <summary>The exact table row a page must carry. The data file is the source; pages are written from it.</summary>
    internal static string FormatRow(CapabilityRow row, string docsPrefix)
    {
        var keys = row.Keys.Count == 0
            ? "\u2014"
            : string.Join(", ", row.Keys.Select(k => "`" + k + "`"));
        return $"| {row.Capability} | {row.Status} | {keys} | {row.Note} | [{row.DocsLabel}]({docsPrefix}{row.Docs}) |";
    }

    /// <summary>
    ///     Walks <c>a.b[].c</c> through <c>properties</c> and <c>items</c>. The schema keeps every object inline, so no
    ///     <c>$ref</c> handling is needed.
    /// </summary>
    private static bool SchemaHasKey(JsonElement root, string key, out string why)
    {
        var node = root;
        var parts = key.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var raw = parts[i];
            var isLast = i == parts.Length - 1;
            var isArray = raw.EndsWith("[]", StringComparison.Ordinal);
            var name = isArray ? raw[..^2] : raw;

            if (!node.TryGetProperty("properties", out var properties) ||
                !properties.TryGetProperty(name, out node))
            {
                why = $"has no property '{name}'.";
                return false;
            }

            if (isArray)
            {
                if (!node.TryGetProperty("items", out node))
                {
                    why = $"'{name}' is not an array.";
                    return false;
                }
            }
            else if (!isLast && node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                     type.GetString() == "array")
            {
                why = $"'{name}' is an array; write '{name}[]'.";
                return false;
            }
        }

        why = string.Empty;
        return true;
    }

    internal sealed class CapabilityFile
    {
        public List<CapabilityRow> Rows { get; set; } = [];
    }

    internal sealed class CapabilityRow
    {
        public string Area { get; set; } = string.Empty;

        public string Capability { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public List<string> Keys { get; set; } = [];

        public string? Schema { get; set; }

        public string Docs { get; set; } = string.Empty;

        public string DocsLabel { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;
    }
}
