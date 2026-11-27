using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class HybridKeyExchangeTests
{
    [TestMethod]
    public void ClientAndServerDeriveTheSameSecret()
    {
        if (!Supported())
        {
            return;
        }

        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519MlKem768);
        using IPrivateKeyShare client = group.GenerateKeyShare(new SystemEntropy());
        byte[] clientShare = new byte[group.PublicKeyLength];
        client.WritePublicKey(clientShare);
        byte[] serverShare = new byte[HybridKeyExchange.ServerShareLength];
        byte[] serverSecret = new byte[group.SharedSecretLength];
        Assert.IsTrue(group.TryEncapsulate(clientShare, new SystemEntropy(), serverShare, serverSecret, out int serverWritten, out int secretWritten));
        Assert.AreEqual(serverShare.Length, serverWritten);
        Assert.AreEqual(serverSecret.Length, secretWritten);
        byte[] clientSecret = new byte[group.SharedSecretLength];
        Assert.IsTrue(client.TryComputeSharedSecret(serverShare, clientSecret, out int clientWritten));
        Assert.AreEqual(clientSecret.Length, clientWritten);
        Assert.IsTrue(clientSecret.AsSpan().SequenceEqual(serverSecret));
    }

    [TestMethod]
    public void WrongLengthsAndADamagedEncapsulationKeyAreRejected()
    {
        if (!Supported())
        {
            return;
        }

        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519MlKem768);
        using IPrivateKeyShare client = group.GenerateKeyShare(new SystemEntropy());
        byte[] clientShare = new byte[group.PublicKeyLength];
        client.WritePublicKey(clientShare);
        clientShare[0] = 0xFF;
        clientShare[1] = 0xFF;
        byte[] serverShare = new byte[HybridKeyExchange.ServerShareLength];
        byte[] secret = new byte[group.SharedSecretLength];
        Assert.IsFalse(group.TryEncapsulate(clientShare, new SystemEntropy(), serverShare, secret, out _, out _));
        Assert.IsFalse(group.TryEncapsulate(new byte[10], new SystemEntropy(), serverShare, secret, out _, out _));
        Assert.IsFalse(client.TryComputeSharedSecret(new byte[10], secret, out _));
    }

    [TestMethod]
    public void DamagedCiphertextDoesNotThrowAndChangesTheSecret()
    {
        if (!Supported())
        {
            return;
        }

        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519MlKem768);
        using IPrivateKeyShare client = group.GenerateKeyShare(new SystemEntropy());
        byte[] clientShare = new byte[group.PublicKeyLength];
        client.WritePublicKey(clientShare);
        byte[] serverShare = new byte[HybridKeyExchange.ServerShareLength];
        byte[] honest = new byte[group.SharedSecretLength];
        Assert.IsTrue(group.TryEncapsulate(clientShare, new SystemEntropy(), serverShare, honest, out _, out _));
        serverShare[0] ^= 0xFF;
        byte[] damaged = new byte[group.SharedSecretLength];
        Assert.IsTrue(client.TryComputeSharedSecret(serverShare, damaged, out _));
        Assert.IsFalse(damaged.AsSpan().SequenceEqual(honest));
    }

    [TestMethod]
    public void SymmetricGroupsDoNotEncapsulate()
    {
        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519);
        byte[] share = new byte[32];
        byte[] secret = new byte[32];
        Assert.IsFalse(group.TryEncapsulate(share, new SystemEntropy(), share, secret, out _, out _));
    }

    [TestMethod]
    public void FidelityFollowsMlKemSupport()
    {
        TlsFingerprintFidelity expected = MLKem.IsSupported ? TlsFingerprintFidelity.Full : TlsFingerprintFidelity.ReducedNoPostQuantum;
        Assert.AreEqual(expected, TlsFingerprint.Current);
    }

    [TestMethod]
    public void OpenSslClientHelloUsesTheSameShareOrder()
    {
        if (!OpenSslOracle.Require(new Version(3, 5)) || !Supported())
        {
            return;
        }

        string openssl = OpenSslOracle.Find() ?? throw new InvalidOperationException("OpenSSL was not found.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = openssl;
        process.StartInfo.Arguments = $"s_client -tls1_3 -connect 127.0.0.1:{port} -groups X25519MLKEM768 -brief";
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();
        using TcpClient client = listener.AcceptTcpClient();
        using NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 5000;
        byte[] buffer = new byte[8192];
        int read = stream.Read(buffer);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        byte[]? share = HybridShare(buffer.AsSpan(0, read));
        Assert.IsNotNull(share);
        Assert.AreEqual(HybridKeyExchange.ClientShareLength, share.Length);
        using MLKem kem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, share.AsSpan(0, HybridKeyExchange.EncapsulationKeyLength));
        CryptographicException? rejected = null;
        try
        {
            MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, share.AsSpan(32, HybridKeyExchange.EncapsulationKeyLength));
        }
        catch (CryptographicException exception)
        {
            rejected = exception;
        }

        Assert.IsNotNull(rejected);
    }

    private static bool Supported()
    {
        if (MLKem.IsSupported)
        {
            return true;
        }

        Assert.Inconclusive("ML-KEM-768 is not available on this runtime.");
        return false;
    }

    private static byte[]? HybridShare(ReadOnlySpan<byte> record)
    {
        if (record.Length < 5 || record[0] != 22)
        {
            return null;
        }

        int length = (record[3] << 8) | record[4];
        ReadOnlySpan<byte> handshake = record.Slice(5, Math.Min(length, record.Length - 5));
        if (handshake.Length < 4 || handshake[0] != 1)
        {
            return null;
        }

        int offset = 4 + 2 + 32;
        int session = handshake[offset];
        offset += 1 + session;
        int ciphers = (handshake[offset] << 8) | handshake[offset + 1];
        offset += 2 + ciphers;
        int compression = handshake[offset];
        offset += 1 + compression;
        int extensions = (handshake[offset] << 8) | handshake[offset + 1];
        offset += 2;
        int end = offset + extensions;
        while (offset + 4 <= end && offset + 4 <= handshake.Length)
        {
            int type = (handshake[offset] << 8) | handshake[offset + 1];
            int size = (handshake[offset + 2] << 8) | handshake[offset + 3];
            offset += 4;
            if (type == 51 && offset + size <= handshake.Length)
            {
                ReadOnlySpan<byte> body = handshake.Slice(offset, size);
                int list = (body[0] << 8) | body[1];
                int index = 2;
                while (index + 4 <= 2 + list && index + 4 <= body.Length)
                {
                    int group = (body[index] << 8) | body[index + 1];
                    int keyLength = (body[index + 2] << 8) | body[index + 3];
                    if (group == 0x11EC && keyLength == HybridKeyExchange.ClientShareLength)
                    {
                        return body.Slice(index + 4, keyLength).ToArray();
                    }

                    index += 4 + keyLength;
                }
            }

            offset += size;
        }

        return null;
    }
}
