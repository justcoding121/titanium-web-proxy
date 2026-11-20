using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>Checks the shape of the wiki pages and the home page, which no compiler or link checker looks at.</summary>
[TestClass]
public partial class DocsStructureTests
{
    private static readonly (string Title, string Link)[] HomePillars =
    [
        ("Embeddable .NET proxy", "/docs/library"),
        ("CLI and Kubernetes edge proxy", "/docs/cli"),
        ("Automatic tunnel", "/docs/features#inspector"),
        ("Visual MITM debugger", "/docs/inspector"),
        ("Benchmarked speed at scale", "/docs/performance"),
        ("Control-plane add-ons", "/docs/plus"),
    ];

    private static readonly string[] PreservedCapabilityTitles =
    [
        "Debug HTTPS traffic",
        "Rewrite and replay",
        "Automatic tunnel",
        "HTTP/1 \u00B7 HTTP/2 \u00B7 HTTP/3",
        "Reverse proxy from the CLI",
        "Zero-downtime reload",
        "Balance and fail over",
        "Secure at the edge",
        "Kubernetes native",
        "Observe everything",
        "Chain and authenticate",
        "Cache, compress, protect",
    ];

    /// <summary>
    ///     The wiki host shows the file name as the page title. A body H1 that says the same thing prints it twice
    ///     (see CONTRIBUTING.md). An H1 that differs on purpose, such as the
    ///     product name on Home, is fine, and so is <c>README.md</c>, which the sync does not publish.
    /// </summary>
    [TestMethod]
    public void WikiPages_DoNotStartWithAnH1ThatRepeatsTheFileName()
    {
        var offenders = new List<string>();
        foreach (var path in DocsRepository.WikiPages())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals("README", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var first = DocsRepository.FirstNonBlankLine(path);
            var heading = TitleH1Regex().Match(first);
            if (heading.Success && Normalize(heading.Groups[1].Value) == Normalize(name))
            {
                offenders.Add($"wiki/{Path.GetFileName(path)}: remove the first line '{first.Trim()}'.");
            }
        }

        Assert.AreEqual(0, offenders.Count, string.Join("\n", offenders));
    }

    [TestMethod]
    public void WikiTitleCheck_MatchesHyphenAndCaseVariants()
    {
        Assert.AreEqual(Normalize("Gateway-API"), Normalize("# Gateway API".TrimStart('#', ' ')));
        Assert.AreEqual(Normalize("HTTP-3"), Normalize("HTTP/3"));
        Assert.AreNotEqual(Normalize("Home"), Normalize("Titanium Web Proxy"));
        Assert.IsFalse(TitleH1Regex().IsMatch("## Capabilities"));
        Assert.IsFalse(TitleH1Regex().IsMatch("> For operators"));
    }

    [TestMethod]
    public void HomePage_ListsTheSixPillarsInOrder_EachLinkingToARealDocsPage()
    {
        var path = Path.Combine(DocsRepository.Root, "website", "index.md");
        var text = File.ReadAllText(path);
        var body = Body(text);

        var majorStart = body.IndexOf("## Major features", StringComparison.Ordinal);
        Assert.IsTrue(majorStart >= 0, "Major features section is present.");
        var afterMajor = body[(majorStart + "## Major features".Length)..];
        var nextHeading = afterMajor.IndexOf("\n## ", StringComparison.Ordinal);
        var section = nextHeading < 0 ? afterMajor : afterMajor[..nextHeading];

        var titles = PillarTitleRegex().Matches(section).Select(m => m.Groups[1].Value.Trim()).ToList();
        CollectionAssert.AreEqual(HomePillars.Select(p => p.Title).ToList(), titles);

        var links = PillarLinkRegex().Matches(section).Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.AreEqual(HomePillars.Length, links.Count, "Every pillar needs a Learn more link.");

        var failures = new List<string>();
        for (var i = 0; i < HomePillars.Length; i++)
        {
            var expected = HomePillars[i].Link;
            var link = links[i];
            if (!string.Equals(link, expected, StringComparison.Ordinal))
            {
                failures.Add($"{titles[i]}: expected {expected}, found {link}.");
            }

            var hash = link.IndexOf('#', StringComparison.Ordinal);
            var page = hash < 0 ? link : link[..hash];
            if (!page.StartsWith("/docs/", StringComparison.Ordinal))
            {
                failures.Add($"{link}: a pillar links to a /docs/ page.");
                continue;
            }

            var file = Path.Combine(DocsRepository.WebsiteDocs, page["/docs/".Length..] + ".md");
            if (!File.Exists(file))
            {
                failures.Add($"{link}: {file} does not exist.");
            }
            else if (hash >= 0 && !DocsRepository.WebsiteHeadingIds(file).Contains(link[(hash + 1)..]))
            {
                failures.Add($"{link}: no heading with that id.");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    [TestMethod]
    public void HomePage_KeepsTheTwelveCapabilityTitlesInTheBody()
    {
        var body = Body(File.ReadAllText(Path.Combine(DocsRepository.Root, "website", "index.md")));
        var missing = PreservedCapabilityTitles.Where(title => !body.Contains(title, StringComparison.Ordinal)).ToList();
        Assert.AreEqual(0, missing.Count, "Missing capability titles: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void HomePage_KeepsEditionLinksDownloadPicksChartAndPerformance()
    {
        var text = File.ReadAllText(Path.Combine(DocsRepository.Root, "website", "index.md"));

        StringAssert.Contains(text, "class=\"edition-grid\"");
        StringAssert.Contains(text, "/download#stable-inspector");
        StringAssert.Contains(text, "/download#stable-cli");
        StringAssert.Contains(text, "/docs/editions");
        StringAssert.Contains(text, "## Performance");
        StringAssert.Contains(text, "rps-practical-linux.png");
        StringAssert.Contains(text, "The Linux tiny-GET chart is below");
        StringAssert.Contains(text, "<HomeQuickStart />");
        StringAssert.Contains(text, "Intercept and inspect HTTP(S) from .NET in a few lines");
        StringAssert.Contains(text, "26,023");
        StringAssert.Contains(text, "non-commercial use only");
        Assert.IsFalse(text.Contains("Commercial use needs a separate arrangement", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HomeQuickStart_LibraryTabIsTheInstallCommandAndGettingStarted()
    {
        var vue = File.ReadAllText(Path.Combine(
            DocsRepository.Root,
            "website",
            ".vitepress",
            "theme",
            "components",
            "HomeQuickStart.vue"));
        StringAssert.Contains(vue, "dotnet add package Titanium.Web.Proxy");
        StringAssert.Contains(vue, "/docs/getting-started");
        Assert.IsFalse(vue.Contains("ExplicitProxyEndPoint", StringComparison.Ordinal));
        StringAssert.Contains(vue, "mkdir conf");
        StringAssert.Contains(vue, "ghcr.io/justcoding121/titanium-web-proxy:latest");
    }

    [TestMethod]
    public void HomeHeroDemo_NotesTheRootCertificateOnFirstRun()
    {
        var vue = File.ReadAllText(Path.Combine(
            DocsRepository.Root,
            "website",
            ".vitepress",
            "theme",
            "components",
            "HomeHeroDemo.vue"));
        StringAssert.Contains(vue, "The first run installs a root certificate");
        StringAssert.Contains(vue, "/docs/security");
        StringAssert.Contains(vue, "26,023");
    }

    [TestMethod]
    public void FrontDoor_DoesNotNamePeerProxyProducts()
    {
        var roots = new[]
        {
            Path.Combine(DocsRepository.Root, "README.md"),
            Path.Combine(DocsRepository.Root, "website"),
        };
        var hits = new List<string>();
        foreach (var root in roots)
        {
            var files = Directory.Exists(root)
                ? Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                : new[] { root };
            foreach (var file in files)
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}.vitepress{Path.DirectorySeparatorChar}cache{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                if (ext is not (".md" or ".vue" or ".mts" or ".css"))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (PeerProxyNameRegex().IsMatch(text))
                {
                    hits.Add(Path.GetRelativePath(DocsRepository.Root, file));
                }
            }
        }

        Assert.AreEqual(0, hits.Count, "Peer product names: " + string.Join(", ", hits));
    }

    private static string Body(string text)
    {
        _ = FrontMatter(text);
        var lines = text.Split('\n');
        var end = Array.FindIndex(lines, 1, l => l.TrimEnd('\r') == "---");
        return string.Join('\n', lines[(end + 1)..]);
    }

    private static string FrontMatter(string text)
    {
        var lines = text.Split('\n');
        Assert.AreEqual("---", lines[0].TrimEnd('\r'), "index.md starts with front matter.");
        var end = Array.FindIndex(lines, 1, l => l.TrimEnd('\r') == "---");
        Assert.IsTrue(end > 0, "front matter is closed.");
        return string.Join('\n', lines[1..end]);
    }

    private static string Normalize(string text) => NonAlphanumericRegex().Replace(text, string.Empty).ToLowerInvariant();

    [GeneratedRegex(@"^# +(.+?)\s*$")]
    private static partial Regex TitleH1Regex();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"<h3>(.+?)</h3>")]
    private static partial Regex PillarTitleRegex();

    [GeneratedRegex(@"href=""(/docs/[^""]+)"">Learn more")]
    private static partial Regex PillarLinkRegex();

    [GeneratedRegex(@"YARP|nginx|HAProxy|Envoy|Traefik|Caddy|Squid|Fiddler|Charles|mitmproxy", RegexOptions.IgnoreCase)]
    private static partial Regex PeerProxyNameRegex();
}
