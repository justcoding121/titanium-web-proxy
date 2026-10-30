using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YamlDotNet.RepresentationModel;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     Runs the YAML examples in <c>website/docs/*.md</c> and <c>wiki/*.md</c> through the loader, the validator, and
///     the schema, so a sample that the CLI would reject cannot ship.
///     <para>
///         Tag a complete example <c>```yaml twp</c>. Tag a snippet that only makes sense next to the rest of a file
///         <c>```yaml twp-fragment</c>; it is skipped. A <c>yaml</c> block with a top-level <c>schemaVersion</c> must
///         carry one of the two tags.
///     </para>
/// </summary>
[TestClass]
public partial class DocExampleTests
{
    private const string Tag = "twp";
    private const string FragmentTag = "twp-fragment";

    [TestMethod]
    public void TaggedExamples_LoadValidateAndMatchTheSchema()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(DocsRepository.SchemaPath));
        var examples = FindExamples().Where(e => e.Kind == ExampleKind.Config).ToList();
        Assert.IsTrue(examples.Count >= 10, $"Expected at least ten twp examples, found {examples.Count}.");

        var failures = new List<string>();
        foreach (var example in examples)
        {
            try
            {
                var config = TwpConfigLoader.LoadYaml(example.Text);
                foreach (var error in TwpConfigValidator.Validate(config))
                {
                    failures.Add($"{example.Where}: {error}");
                }

                var stream = new YamlStream();
                stream.Load(new StringReader(example.Text));
                if (stream.Documents.Count > 0)
                {
                    CheckKeys(stream.Documents[0].RootNode, schema.RootElement, "$", example.Where, failures);
                }
            }
            catch (Exception ex) when (ex is not AssertFailedException)
            {
                failures.Add($"{example.Where}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.AreEqual(0, failures.Count, "Doc examples the CLI would reject:\n" + string.Join("\n", failures));
    }

    [TestMethod]
    public void YamlBlockWithSchemaVersion_IsTaggedTwpOrTwpFragment()
    {
        var untagged = FindExamples()
            .Where(e => e.Kind == ExampleKind.Untagged && SchemaVersionLineRegex().IsMatch(e.Text))
            .Select(e => e.Where)
            .ToList();

        Assert.AreEqual(
            0,
            untagged.Count,
            "These blocks look like a whole twp.yaml; tag them 'yaml twp' (or 'yaml twp-fragment'):\n" + string.Join("\n", untagged));
    }

    [TestMethod]
    public void FragmentsAreSkipped_AndOnlyTagSpellingsWeKnowAreUsed()
    {
        var unknown = FindExamples()
            .Where(e => e.Kind == ExampleKind.UnknownTag)
            .Select(e => e.Where + " " + e.Info)
            .ToList();

        Assert.AreEqual(0, unknown.Count, "Unknown twp tag:\n" + string.Join("\n", unknown));
    }

    [TestMethod]
    public void Extractor_FindsTaggedBlocksAndSkipsFragments()
    {
        const string markdown = """
            # Page

            ```yaml twp
            schemaVersion: "7.1"
            ```

            ```yaml twp-fragment
            routes: []
            ```

            ```yaml
            other: 1
            ```

            - item

              ```yaml twp
              listeners: []
              ```
            """;

        var found = Extract("sample.md", markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToArray()).ToList();

        CollectionAssert.AreEqual(
            new[] { ExampleKind.Config, ExampleKind.Fragment, ExampleKind.Untagged, ExampleKind.Config },
            found.Select(e => e.Kind).ToArray());
        Assert.AreEqual("listeners: []", found[3].Text.Trim());
    }

    [TestMethod]
    public void KeyCheck_RejectsAMisspelledKey()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(DocsRepository.SchemaPath));
        var stream = new YamlStream();
        stream.Load(new StringReader("routes:\n  - id: a\n    retry:\n      attemps: 2\n"));
        var failures = new List<string>();

        CheckKeys(stream.Documents[0].RootNode, schema.RootElement, "$", "sample", failures);

        Assert.AreEqual(1, failures.Count);
        StringAssert.Contains(failures[0], "attemps");
    }

    private static IEnumerable<Example> FindExamples()
    {
        foreach (var path in DocsRepository.WebsiteDocPages().Concat(DocsRepository.WikiPages()))
        {
            var relative = Path.GetRelativePath(DocsRepository.Root, path).Replace('\\', '/');
            foreach (var example in Extract(relative, File.ReadAllLines(path)))
            {
                yield return example;
            }
        }
    }

    /// <summary>Finds every fenced <c>yaml</c> block, with the tag it carries.</summary>
    private static IEnumerable<Example> Extract(string file, string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var open = OpenFenceRegex().Match(lines[i]);
            if (!open.Success)
            {
                continue;
            }

            var indent = open.Groups[1].Length;
            var fence = open.Groups[2].Value;
            var language = open.Groups[3].Value;
            var info = open.Groups[4].Value.Trim();
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (lines[j].TrimStart().StartsWith(fence, StringComparison.Ordinal) && lines[j].Trim().Trim(fence[0]).Length == 0)
                {
                    break;
                }

                body.Add(lines[j].Length >= indent ? lines[j][indent..] : lines[j].TrimStart());
            }

            if (language is "yaml" or "yml")
            {
                var tags = info.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.StartsWith(Tag, StringComparison.Ordinal))
                    .ToList();
                var kind = tags.Count == 0 ? ExampleKind.Untagged
                    : tags[0] == Tag ? ExampleKind.Config
                    : tags[0] == FragmentTag ? ExampleKind.Fragment
                    : ExampleKind.UnknownTag;
                yield return new Example($"{file}:{i + 1}", kind, info, string.Join('\n', body));
            }

            i = j;
        }
    }

    /// <summary>
    ///     Every mapping key must be a property the schema names, wherever the schema says properties are closed.
    ///     Free-form maps (<c>plus.options</c>, plugin <c>settings</c>) have no <c>properties</c> and are not checked.
    /// </summary>
    private static void CheckKeys(YamlNode node, JsonElement schema, string path, string where, List<string> failures)
    {
        switch (node)
        {
            case YamlMappingNode map when schema.TryGetProperty("properties", out var properties):
                var closed = schema.TryGetProperty("additionalProperties", out var extra) &&
                             extra.ValueKind == JsonValueKind.False;
                foreach (var entry in map.Children)
                {
                    var key = (entry.Key as YamlScalarNode)?.Value ?? string.Empty;
                    if (properties.TryGetProperty(key, out var child))
                    {
                        CheckKeys(entry.Value, child, path + "." + key, where, failures);
                    }
                    else if (closed)
                    {
                        failures.Add($"{where}: '{key}' is not a key of {path} in twp.schema.json.");
                    }
                }

                break;
            case YamlSequenceNode sequence when schema.TryGetProperty("items", out var items):
                foreach (var item in sequence.Children)
                {
                    CheckKeys(item, items, path + "[]", where, failures);
                }

                break;
        }
    }

    [GeneratedRegex(@"^(\s*)(```+|~~~+)\s*([\w-]*)\s*(.*)$")]
    private static partial Regex OpenFenceRegex();

    [GeneratedRegex(@"^schemaVersion\s*:", RegexOptions.Multiline)]
    private static partial Regex SchemaVersionLineRegex();

    private enum ExampleKind
    {
        Config,
        Fragment,
        Untagged,
        UnknownTag,
    }

    private sealed record Example(string Where, ExampleKind Kind, string Info, string Text);
}
