using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LimitCatalogConsistencyTests
{
    [TestMethod]
    public void EveryCatalogEntry_HasApplierValidatorAndDocs()
    {
        var applier = File.ReadAllText(FindRepoFile(Path.Combine("src", "Titanium.Cli", "Config", "ServerConfigApplier.cs")));
        var validator = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "Titanium.Web.Proxy.Configuration", "TwpConfigValidator.cs")));
        var docs = File.ReadAllText(FindRepoFile(Path.Combine("website", "docs", "limits.md")));

        foreach (var entry in LimitCatalog.All)
        {
            StringAssert.Contains(applier, entry.Property, $"applier missing {entry.Property}");
            StringAssert.Contains(validator, entry.Property, $"validator missing {entry.Property}");
            StringAssert.Contains(docs, entry.CliKey, $"docs missing {entry.CliKey}");
        }
    }

    private static string FindRepoFile(string relativeUnderRepo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeUnderRepo);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        Assert.Fail($"Could not locate {relativeUnderRepo} from {AppContext.BaseDirectory}");
        return null!;
    }
}
