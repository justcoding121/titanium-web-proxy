using Microsoft.VisualStudio.TestTools.UnitTesting;
using YamlDotNet.RepresentationModel;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     Keeps <c>tests/smoke-matrix.yml</c> readable and nudges contributors when a supported capability has no smoke yet.
///     Missing entries are logged only; a missing or unreadable matrix file fails the build.
/// </summary>
[TestClass]
public class SmokeMatrixCoverageTests
{
    private static string MatrixPath => Path.Combine(DocsRepository.Root, "tests", "smoke-matrix.yml");

    [TestMethod]
    public void SmokeMatrix_IsPresent_AndMissingEntriesAreLogged()
    {
        Assert.IsTrue(File.Exists(MatrixPath), $"smoke matrix not found: {MatrixPath}");

        var entries = LoadEntries(MatrixPath);
        Assert.IsTrue(entries.Count > 0, "smoke-matrix.yml must list at least one entry.");

        var missing = entries.Where(e => e.Status.Equals("missing", StringComparison.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"smoke-matrix.yml: {missing.Count} entr{(missing.Count == 1 ? "y" : "ies")} still missing automated smoke:");
            foreach (var entry in missing)
            {
                Console.WriteLine($"  - {entry.Id}: {entry.Capability}");
            }
        }

        Assert.IsTrue(true, "missing smoke entries are advisory until the matrix is fully wired.");
    }

    [TestMethod]
    public void SmokeMatrix_CoveredEntries_ReferenceNonEmptyTests()
    {
        Assert.IsTrue(File.Exists(MatrixPath), $"smoke matrix not found: {MatrixPath}");

        var covered = LoadEntries(MatrixPath)
            .Where(e => e.Status.Equals("covered", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.IsTrue(covered.Count > 0, "expected at least one covered smoke entry.");

        foreach (var entry in covered)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Test),
                $"{entry.Id}: covered entries must name the test or workflow (test:).");
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Job),
                $"{entry.Id}: covered entries must name a job (job:).");
        }
    }

    private static List<SmokeEntry> LoadEntries(string path)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(path))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var entriesNode = (YamlSequenceNode)root.Children[new YamlScalarNode("entries")];
        var list = new List<SmokeEntry>();
        foreach (var child in entriesNode.Children)
        {
            var map = (YamlMappingNode)child;
            list.Add(new SmokeEntry(
                Scalar(map, "id"),
                Scalar(map, "capability"),
                Scalar(map, "status"),
                Scalar(map, "test"),
                Scalar(map, "job")));
        }

        return list;
    }

    private static string Scalar(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlScalarNode scalar)
        {
            return string.Empty;
        }

        return scalar.Value ?? string.Empty;
    }

    private sealed record SmokeEntry(string Id, string Capability, string Status, string Test, string Job);
}
