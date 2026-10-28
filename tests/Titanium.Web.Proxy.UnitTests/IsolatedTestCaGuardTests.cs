using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public partial class IsolatedTestCaGuardTests
{
    [GeneratedRegex(@"new\s+CertificateManager\(\s*null\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultSubjectManager();

    /// <summary>
    ///     Tests must not mint a root with the product-default subject: a same-subject root already in
    ///     <c>CurrentUser\Root</c> (Inspector, examples) breaks Windows chain building locally while CI
    ///     stays green. Use <see cref="IsolatedTestCa" />.
    /// </summary>
    [TestMethod]
    public void UnitAndIntegrationTests_DoNotCreateDefaultSubjectRoots() => AssertNoDefaultSubjectRoots();

    private static void AssertNoDefaultSubjectRoots([CallerFilePath] string thisFile = "")
    {
        var unitDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(unitDir)!;
        var integrationDir = Path.Combine(testsDir, "Titanium.Web.Proxy.IntegrationTests");
        if (!Directory.Exists(unitDir) || !Directory.Exists(integrationDir))
            Assert.Inconclusive("Test sources are not available next to the build output.");

        var offenders = new[] { unitDir, integrationDir }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.EndsWith("IsolatedTestCaGuardTests.cs", StringComparison.Ordinal))
            .Where(f => DefaultSubjectManager().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.AreEqual(0, offenders.Length,
            "Use IsolatedTestCa.NewRootName() instead of a null root name in: " + string.Join(", ", offenders));
    }
}

