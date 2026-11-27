using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class WycheproofEd25519Tests
{
    [TestMethod]
    public void VerifyMatchesWycheproofEd25519()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(WycheproofX25519Tests.FindFixture("ed25519_test.json")));
        int valid = 0;
        foreach (JsonElement group in document.RootElement.GetProperty("testGroups").EnumerateArray())
        {
            JsonElement key = group.GetProperty("publicKey");
            if (key.GetProperty("curve").GetString() != "edwards25519")
            {
                continue;
            }

            byte[] publicKey = Convert.FromHexString(key.GetProperty("pk").GetString()!);
            foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
            {
                byte[] message = Convert.FromHexString(test.GetProperty("msg").GetString()!);
                byte[] signature = Convert.FromHexString(test.GetProperty("sig").GetString()!);
                bool verified = Ed25519Verifier.Verify(publicKey, message, signature);
                string result = test.GetProperty("result").GetString()!;
                int id = test.GetProperty("tcId").GetInt32();
                if (result == "valid")
                {
                    Assert.IsTrue(verified, "tcId " + id);
                    valid++;
                }
                else if (result == "invalid")
                {
                    Assert.IsFalse(verified, "tcId " + id);
                }
            }
        }

        Assert.IsTrue(valid > 0);
    }
}
