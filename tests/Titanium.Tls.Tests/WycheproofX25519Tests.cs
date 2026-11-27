using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class WycheproofX25519Tests
{
    [TestMethod]
    public void ScalarMultMatchesWycheproofX25519()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FindFixture("x25519_test.json")));
        int valid = 0;
        Span<byte> shared = stackalloc byte[32];
        foreach (JsonElement group in document.RootElement.GetProperty("testGroups").EnumerateArray())
        {
            foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
            {
                byte[] scalar = Convert.FromHexString(test.GetProperty("private").GetString()!);
                byte[] point = Convert.FromHexString(test.GetProperty("public").GetString()!);
                byte[] expected = Convert.FromHexString(test.GetProperty("shared").GetString()!);
                shared.Clear();
                bool agreed = scalar.Length == 32 && point.Length == 32 && X25519.TryAgree(scalar, point, shared);
                string result = test.GetProperty("result").GetString()!;
                if (result == "valid")
                {
                    Assert.IsTrue(agreed, "tcId " + test.GetProperty("tcId").GetInt32());
                    Assert.IsTrue(shared.SequenceEqual(expected), "tcId " + test.GetProperty("tcId").GetInt32());
                    valid++;
                }
                else if (agreed)
                {
                    Assert.IsTrue(shared.SequenceEqual(expected), "tcId " + test.GetProperty("tcId").GetInt32());
                }
            }
        }

        Assert.IsTrue(valid > 0);
    }

    internal static string FindFixture(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Titanium.Tls.Tests", "Fixtures", "wycheproof", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail(name + " was not found.");
        return string.Empty;
    }
}
