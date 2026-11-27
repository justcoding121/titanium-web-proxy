using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class WycheproofAeadTests
{
    [TestMethod]
    public void AesGcmMatchesWycheproofFor96BitNonces()
    {
        AssertAead("aes_gcm_test.json", (key, nonce, ciphertext, tag, plaintext, aad) =>
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        });
    }

    [TestMethod]
    public void ChaCha20Poly1305MatchesWycheproofFor96BitNonces()
    {
        AssertAead("chacha20_poly1305_test.json", (key, nonce, ciphertext, tag, plaintext, aad) =>
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        });
    }

    private static void AssertAead(string file, Decrypt decrypt)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(WycheproofX25519Tests.FindFixture(file)));
        int checkedVectors = 0;
        foreach (JsonElement group in document.RootElement.GetProperty("testGroups").EnumerateArray())
        {
            if (group.GetProperty("ivSize").GetInt32() != 96)
            {
                continue;
            }

            foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
            {
                byte[] key = Convert.FromHexString(test.GetProperty("key").GetString()!);
                byte[] nonce = Convert.FromHexString(test.GetProperty("iv").GetString()!);
                byte[] aad = Convert.FromHexString(test.GetProperty("aad").GetString()!);
                byte[] ciphertext = Convert.FromHexString(test.GetProperty("ct").GetString()!);
                byte[] tag = Convert.FromHexString(test.GetProperty("tag").GetString()!);
                byte[] message = Convert.FromHexString(test.GetProperty("msg").GetString()!);
                byte[] plaintext = new byte[message.Length];
                string result = test.GetProperty("result").GetString()!;
                try
                {
                    decrypt(key, nonce, ciphertext, tag, plaintext, aad);
                    Assert.AreNotEqual("invalid", result, "tcId " + test.GetProperty("tcId").GetInt32());
                    if (result == "valid")
                    {
                        Assert.IsTrue(plaintext.AsSpan().SequenceEqual(message), "tcId " + test.GetProperty("tcId").GetInt32());
                    }
                }
                catch (AuthenticationTagMismatchException) when (result != "valid")
                {
                }
                catch (ArgumentException) when (result != "valid")
                {
                }

                checkedVectors++;
            }
        }

        Assert.IsTrue(checkedVectors > 0);
    }

    private delegate void Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, byte[] aad);
}
