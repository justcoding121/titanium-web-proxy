using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class HarnessTests
{
    [TestMethod]
    public void OpenSslOnThisMachineReportsAtLeast35()
    {
        if (!OpenSslOracle.Require(new Version(3, 5)))
        {
            Assert.Inconclusive("OpenSSL 3.5 or later was not found.");
        }
    }

    [TestMethod]
    public void FixedEntropyReturnsScriptedBytesAndThenFails()
    {
        FixedEntropy entropy = new(new byte[] { 1, 2, 3, 4 });
        byte[] first = new byte[2];
        entropy.Fill(first);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, first);
        Assert.ThrowsException<InvalidOperationException>(() => entropy.Fill(new byte[3]));
    }

    [TestMethod]
    public void PublicTypeNamesDoNotCollideWithTheBcl()
    {
        HashSet<string> bcl = new(StringComparer.Ordinal);
        foreach (string assemblyName in new[] { "System.Net.Security", "System.Net.Quic", "System.Net.Sockets", "System.Security.Authentication" })
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            foreach (Type type in assembly.GetExportedTypes())
            {
                bcl.Add(type.Name);
            }
        }

        foreach (Type type in typeof(AssemblyMarker).Assembly.GetExportedTypes())
        {
            Assert.IsFalse(bcl.Contains(type.Name), type.FullName);
        }
    }

    [TestMethod]
    public void TraceabilityRowsMatchTheTestsThatClaimThem()
    {
        string path = FindTraceability();
        var rows = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (!line.StartsWith('|'))
            {
                continue;
            }

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 6 || cells[1] == "RFC" || cells[1].Contains("---", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(cells[4]);
        }

        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in typeof(HarnessTests).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                methods.Add(type.Name + "." + method.Name);
            }
        }

        foreach (string testId in rows)
        {
            Assert.IsTrue(methods.Contains(testId), testId);
        }

        foreach (Type type in typeof(HarnessTests).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (RfcRequirementAttribute claim in method.GetCustomAttributes<RfcRequirementAttribute>())
                {
                    Assert.IsTrue(rows.Contains(claim.Id), claim.Id);
                }
            }
        }
    }

    private static string FindTraceability()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Titanium.Tls", "design", "TRACEABILITY.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail("TRACEABILITY.md was not found.");
        return string.Empty;
    }
}
