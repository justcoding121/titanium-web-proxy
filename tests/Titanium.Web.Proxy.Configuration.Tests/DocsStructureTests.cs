using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>Checks the shape of the wiki pages and the home page, which no compiler or link checker looks at.</summary>
[TestClass]
public partial class DocsStructureTests
{
    private static readonly string[] HomeTiles =
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
    public void HomePage_ListsTheTwelveTilesInOrder_EachLinkingToARealDocsPage()
    {
        var path = Path.Combine(DocsRepository.Root, "website", "index.md");
        var text = File.ReadAllText(path);
        var frontMatter = FrontMatter(text);

        var titles = TileTitleRegex().Matches(frontMatter).Select(m => m.Groups[1].Value.Trim()).ToList();
        CollectionAssert.AreEqual(HomeTiles, titles);

        var links = TileLinkRegex().Matches(frontMatter).Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.AreEqual(HomeTiles.Length, links.Count, "Every tile needs a link.");

        var failures = new List<string>();
        foreach (var link in links)
        {
            var hash = link.IndexOf('#', StringComparison.Ordinal);
            var page = hash < 0 ? link : link[..hash];
            if (!page.StartsWith("/docs/", StringComparison.Ordinal))
            {
                failures.Add($"{link}: a tile links to a /docs/ page.");
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

    [GeneratedRegex(@"(?m)^  - title: (.+?)\r?$")]
    private static partial Regex TileTitleRegex();

    [GeneratedRegex(@"(?m)^    link: (.+?)\r?$")]
    private static partial Regex TileLinkRegex();
}
