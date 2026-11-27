using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class Ed25519OpenSslTests
{
    [TestMethod]
    public void VerifyMatchesOpenSslSignatures()
    {
        if (!OpenSslOracle.Require(new Version(3, 0)))
        {
            Assert.Inconclusive("OpenSSL 3 was not found.");
            return;
        }

        string openssl = OpenSslOracle.Find() ?? throw new InvalidOperationException("OpenSSL was not found.");
        string dir = Path.Combine(Path.GetTempPath(), "twp-ed25519-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string keyPath = Path.Combine(dir, "key.der");
            string publicPath = Path.Combine(dir, "pub.der");
            string messagePath = Path.Combine(dir, "msg.bin");
            string signaturePath = Path.Combine(dir, "sig.bin");
            Run(openssl, $"genpkey -algorithm ED25519 -outform DER -out \"{keyPath}\"");
            Run(openssl, $"pkey -in \"{keyPath}\" -inform DER -pubout -outform DER -out \"{publicPath}\"");
            byte[] publicKey = File.ReadAllBytes(publicPath);
            Assert.AreEqual(44, publicKey.Length);
            publicKey = publicKey[^32..];
            byte[] message = new byte[48];
            for (int i = 0; i < 8; i++)
            {
                RandomNumberGenerator.Fill(message);
                File.WriteAllBytes(messagePath, message);
                Run(openssl, $"pkeyutl -sign -inkey \"{keyPath}\" -keyform DER -rawin -in \"{messagePath}\" -out \"{signaturePath}\"");
                byte[] signature = File.ReadAllBytes(signaturePath);
                Assert.AreEqual(64, signature.Length);
                Assert.IsTrue(Ed25519Verifier.Verify(publicKey, message, signature), "sample " + i);
                signature[20] ^= 0xFF;
                Assert.IsFalse(Ed25519Verifier.Verify(publicKey, message, signature), "sample " + i);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Run(string openssl, string arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = openssl,
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException("OpenSSL Ed25519 command failed. " + error);
        }
    }
}
