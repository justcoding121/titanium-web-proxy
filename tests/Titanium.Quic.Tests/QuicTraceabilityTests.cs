using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicTraceabilityTests
{
    [TestMethod]
    public void EveryNamedTestExists()
    {
        string path = FindTraceability();
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("| RFC ", StringComparison.Ordinal) || line.Contains("---", StringComparison.Ordinal))
            {
                continue;
            }

            string[] cells = line.Split('|');
            if (cells.Length < 6)
            {
                continue;
            }

            string testId = cells[4].Trim();
            if (testId.Length == 0 || testId.Equals("Test id", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(testId);
        }

        Assert.IsTrue(rows.Count > 0, path);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in typeof(QuicTraceabilityTests).Assembly.GetTypes())
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
    }

    private static string FindTraceability()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Titanium.Quic", "design", "TRACEABILITY.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("TRACEABILITY.md");
    }
}
