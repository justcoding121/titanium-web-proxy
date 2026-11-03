using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public partial class TestHygieneLintTests
{
    [GeneratedRegex(@"BindAddress\s*=\s*IPAddress\.(Any|IPv6Any)\b")]
    private static partial Regex BindAddressAny();

    [GeneratedRegex(@"\.Bind\(\s*new IPEndPoint\(\s*IPAddress\.(Any|IPv6Any)\b")]
    private static partial Regex BindEndpointAny();

    [GeneratedRegex(@"TcpListener\(\s*IPAddress\.(Any|IPv6Any)\b")]
    private static partial Regex TcpListenerAny();

    [GeneratedRegex(@"TcpListener\.Create\s*\(")]
    private static partial Regex TcpListenerCreate();

    [GeneratedRegex(@"host:\s*""?(0\.0\.0\.0|::|\*)""?")]
    private static partial Regex WildcardHost();

    private static readonly string[] SharedSuppressRootStoreUiPath =
    [
        "tests/Titanium.TestKit/Shared/SuppressRootStoreUiModuleInit.cs",
    ];

    [TestMethod]
    public void WildcardBindSample_IsReported()
    {
        var hits = FindHits("sample.cs", "options.BindAddress = IPAddress." + "IPv6Any;");
        Assert.AreEqual(1, hits.Count);
    }

    [TestMethod]
    public void Tests_DoNotBindAWildcardWithoutAnAllowlistReason()
    {
        var root = FindRepoRoot();
        var allow = LoadAllowlist(Path.Combine(root, "tests", "test-hygiene-allowlist.json"));
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var hit in FindHits(relative, File.ReadAllText(file)))
            {
                if (!allow.Any(entry =>
                        string.Equals(entry.File, relative, StringComparison.OrdinalIgnoreCase) &&
                        hit.Contains(entry.Contains, StringComparison.Ordinal)))
                {
                    hits.Add(hit);
                }
            }
        }

        Assert.AreEqual(0, hits.Count, string.Join(Environment.NewLine, hits));
    }

    [TestMethod]
    public void EveryTestProject_LinksTheSharedInitializerAndRunsettings()
    {
        var root = FindRepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "tests", "Directory.Build.props"));
        StringAssert.Contains(props, "test.runsettings");
        StringAssert.Contains(props, "SuppressRootStoreUiModuleInit.cs");
        StringAssert.Contains(props, "TwpStoreLeakGuard.cs");

        var copies = Directory.EnumerateFiles(Path.Combine(root, "tests"), "SuppressRootStoreUiModuleInit.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();
        CollectionAssert.AreEqual(SharedSuppressRootStoreUiPath, copies);

        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (name is "Titanium.TestKit" or "Titanium.Web.Proxy.TestPlugins")
            {
                continue;
            }

            var xml = File.ReadAllText(project);
            StringAssert.Contains(xml, "Microsoft.NET.Test.Sdk", $"{name} is not a test project");
        }
    }

    private static List<string> FindHits(string relativePath, string text)
    {
        var hits = new List<string>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            if (BindAddressAny().IsMatch(line) ||
                BindEndpointAny().IsMatch(line) ||
                TcpListenerAny().IsMatch(line) ||
                TcpListenerCreate().IsMatch(line) ||
                line.Contains("Listen" + "AnyIP", StringComparison.Ordinal) ||
                line.Contains("http://" + "+", StringComparison.Ordinal) ||
                WildcardHost().IsMatch(line))
            {
                hits.Add($"{relativePath}:{lineNumber}: {line.Trim()}");
            }
        }

        return hits;
    }

    private static List<AllowEntry> LoadAllowlist(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => new AllowEntry(
                entry.GetProperty("file").GetString() ?? "",
                entry.GetProperty("contains").GetString() ?? ""))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("Could not find the repository root from the test output directory.");
        return "";
    }

    private sealed record AllowEntry(string File, string Contains);
}
