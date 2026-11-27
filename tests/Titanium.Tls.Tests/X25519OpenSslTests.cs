using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class X25519OpenSslTests
{
    private static readonly byte[] PrivatePrefix = Convert.FromHexString("302e020100300506032b656e04220420");
    private static readonly byte[] PublicPrefix = Convert.FromHexString("302a300506032b656e032100");

    [TestMethod]
    public void ScalarMultMatchesOpenSslOnFiftySamples()
    {
        if (!OpenSslOracle.Require(new Version(3, 5)))
        {
            Assert.Inconclusive("OpenSSL 3.5 or later was not found.");
            return;
        }

        string openssl = OpenSslOracle.Find() ?? throw new InvalidOperationException("OpenSSL was not found.");
        string dir = Path.Combine(Path.GetTempPath(), "twp-x25519-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string privPath = Path.Combine(dir, "priv.der");
            string peerPath = Path.Combine(dir, "peer.der");
            string sharedPath = Path.Combine(dir, "shared.bin");
            byte[] scalar = new byte[32];
            byte[] peerScalar = new byte[32];
            Span<byte> peerPublic = stackalloc byte[32];
            Span<byte> shared = stackalloc byte[32];
            for (int i = 0; i < 50; i++)
            {
                RandomNumberGenerator.Fill(scalar);
                RandomNumberGenerator.Fill(peerScalar);
                X25519.PublicKey(peerScalar, peerPublic);
                File.WriteAllBytes(privPath, Concat(PrivatePrefix, scalar));
                File.WriteAllBytes(peerPath, Concat(PublicPrefix, peerPublic));
                Derive(openssl, privPath, peerPath, sharedPath);
                Assert.IsTrue(X25519.TryAgree(scalar, peerPublic, shared), $"sample {i} was rejected");
                Assert.IsTrue(shared.SequenceEqual(File.ReadAllBytes(sharedPath)), $"sample {i}");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Derive(string openssl, string privPath, string peerPath, string sharedPath)
    {
        if (File.Exists(sharedPath))
        {
            File.Delete(sharedPath);
        }

        ProcessStartInfo start = new()
        {
            FileName = openssl,
            Arguments = $"pkeyutl -derive -inkey \"{privPath}\" -keyform DER -peerkey \"{peerPath}\" -peerform DER -out \"{sharedPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException("OpenSSL X25519 derive failed. " + error);
        }
    }

    private static byte[] Concat(byte[] prefix, ReadOnlySpan<byte> body)
    {
        byte[] result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
