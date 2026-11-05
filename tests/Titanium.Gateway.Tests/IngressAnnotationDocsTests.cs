using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     The annotation table in <c>website/docs/ingress.md</c> is generated from <see cref="IngressAnnotations.All"/>.
///     Set <c>TITANIUM_UPDATE_DOCS=1</c> and run these tests to rewrite the block after changing the table.
/// </summary>
[TestClass]
public partial class IngressAnnotationDocsTests
{
    [GeneratedRegex("\"((?:Unknown|Invalid|Unsupported|Backend|Route|Canary|Port)[A-Za-z]*)\"")]
    private static partial Regex EventReasonPattern();
    private const string Start = "<!-- annotations:start -->";
    private const string End = "<!-- annotations:end -->";

    private static string DocsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "website", "docs", "ingress.md")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "website/docs/ingress.md was not found above the test directory");
        return Path.Combine(directory.FullName, "website", "docs", "ingress.md");
    }

    private static string Block(string text)
    {
        var start = text.IndexOf(Start, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "the annotation markers are missing from ingress.md");
        return text[(start + Start.Length)..end];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    [TestMethod]
    public void TheDocsTableIsTheGeneratedTable()
    {
        var path = DocsPath();
        var text = File.ReadAllText(path);
        var generated = IngressAnnotations.RenderMarkdownTable();

        if (Environment.GetEnvironmentVariable("TITANIUM_UPDATE_DOCS") == "1")
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var start = text.IndexOf(Start, StringComparison.Ordinal) + Start.Length;
            var end = text.IndexOf(End, StringComparison.Ordinal);
            var updated = text[..start] + "\n" + generated.Replace("\n", newline, StringComparison.Ordinal).TrimEnd() + "\n" + text[end..];
            File.WriteAllText(path, updated.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal));
            text = File.ReadAllText(path);
        }

        Assert.AreEqual(Normalize(generated), Normalize(Block(text)), "docs/ingress.md is out of date; run the tests with TITANIUM_UPDATE_DOCS=1");
    }

    [TestMethod]
    public void EveryAnnotationAppearsInTheTable_ExactlyOnce()
    {
        var table = Normalize(Block(File.ReadAllText(DocsPath())));

        foreach (var spec in IngressAnnotations.All)
        {
            var row = $"| `{spec.Key}` |";
            var first = table.IndexOf(row, StringComparison.Ordinal);
            Assert.IsTrue(first >= 0, $"{spec.Key} is not documented");
            Assert.AreEqual(first, table.LastIndexOf(row, StringComparison.Ordinal), $"{spec.Key} is documented twice");
        }

        var rows = table.Split('\n').Count(line => line.StartsWith("| `", StringComparison.Ordinal));
        Assert.AreEqual(IngressAnnotations.All.Count, rows, "the table has a row that is not an annotation");
    }

    [TestMethod]
    public void EveryAnnotationHasAValueAFeatureAndANote()
    {
        foreach (var spec in IngressAnnotations.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(spec.Values), spec.Name + " value");
            Assert.IsFalse(string.IsNullOrWhiteSpace(spec.Feature), spec.Name + " feature");
            Assert.IsFalse(string.IsNullOrWhiteSpace(spec.Notes), spec.Name + " notes");
            Assert.IsFalse(spec.Values.Contains('|', StringComparison.Ordinal) || spec.Feature.Contains('|', StringComparison.Ordinal) || spec.Notes.Contains('|', StringComparison.Ordinal), spec.Name + " has a pipe that would break the table");
        }
    }

    [TestMethod]
    public void TheNamesAreUnique_AndEveryOneIsKnownAndParses()
    {
        Assert.AreEqual(IngressAnnotations.All.Count, IngressAnnotations.All.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var spec in IngressAnnotations.All)
        {
            Assert.IsTrue(IngressAnnotations.IsKnown(spec.Name), spec.Name);
            var parsed = IngressAnnotations.Parse(new Dictionary<string, string> { [spec.Key] = "x" }, "default");
            Assert.AreEqual(0, parsed.Unknown.Count, spec.Name + " is listed but parsed as unknown");
        }
    }

    [TestMethod]
    public void AnAnnotationOutsideTheTable_IsReportedAsUnknown()
    {
        var parsed = IngressAnnotations.Parse(new Dictionary<string, string> { [IngressAnnotations.Prefix + "not-a-thing"] = "x", ["example.com/other"] = "y" }, "default");

        CollectionAssert.AreEqual(new[] { IngressAnnotations.Prefix + "not-a-thing" }, parsed.Unknown.ToArray());
    }

    [TestMethod]
    public void TheDocsNameEveryEventReasonTheControllerCanRaise()
    {
        var text = File.ReadAllText(DocsPath());
        var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(DocsPath())!, "..", "..", "src", "Titanium.Gateway", "Translation", "TranslationPass.Ingress.cs"));

        var reasons = EventReasonPattern().Matches(source)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(r => r is "UnknownAnnotation" or "InvalidAnnotation" or "PortUnavailable" or "InvalidTLS" or "InvalidPath" or "RouteConflict"
                or "CanaryWithoutPrimary" or "CanaryConflict" or "UnsupportedBackend" or "InvalidBackend" or "BackendNotFound");

        foreach (var reason in reasons)
        {
            StringAssert.Contains(text, $"`{reason}`", reason + " is raised by the controller but missing from the docs");
        }
    }
}
