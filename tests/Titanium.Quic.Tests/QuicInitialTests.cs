using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicInitialTests
{
    private static readonly byte[] ConnectionId = Convert.FromHexString("8394c8f03e515708");

    [TestMethod]
    public void ClientAndServerInitialKeysMatchAppendixA()
    {
        AssertSide(client: true,
            "c00cf151ca5be075ed0ebfb5c80323c42d6b7db67881289af4008f1f6c357aea",
            "1f369613dd76d5467730efcbe3b1a22d",
            "fa044b2f42a3fd3b46fb255c",
            "9f50449e04a0e810283a1e9933adedd2");
        AssertSide(client: false,
            "3c199828fd139efd216c155ad844cc81fb82fa8d7446fa7d78be803acdda951b",
            "cf3a5331653c364c88f0f379b6067e37",
            "0ac1493ca1905853b0bba03e",
            "c206b8d9b9f0f37644430b490eeaa314");
    }

    [TestMethod]
    public void AesHeaderProtectionRoundTripsTheAppendixHeaders()
    {
        AssertHeader(packetNumberOffset: 18,
            unprotected: "c300000001088394c8f03e5157080000449e00000002",
            sample: "d1b1c98dd7689fb8ec11d242b123dc9b",
            protectedHeader: "c000000001088394c8f03e5157080000449e7b9aec34",
            headerKey: "9f50449e04a0e810283a1e9933adedd2");
        AssertHeader(packetNumberOffset: 18,
            unprotected: "c1000000010008f067a5502a4262b50040750001",
            sample: "2cd0991cd25b0aac406a5816b6394100",
            protectedHeader: "cf000000010008f067a5502a4262b5004075c0d9",
            headerKey: "c206b8d9b9f0f37644430b490eeaa314");
    }

    [TestMethod]
    public void ChaCha20BlockMatchesRfc8439()
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)i;
        }

        byte[] nonce = [0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x4a, 0x00, 0x00, 0x00, 0x00];
        var block = new byte[64];
        ChaCha20Block.Write(key, counter: 1, nonce, block);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "10f1e7e4d13b5915500fdd1fa32071c4" +
            "c7d1f4c733c068030422aa9ac3d46c4e" +
            "d2826446079faa0914c2d705d98b02a2" +
            "b5129cd1de164eb9cbd083e8a2503c4e"), block);
    }

    [TestMethod]
    public void ChaCha20HeaderProtectionMatchesAppendixA()
    {
        byte[] secret = Convert.FromHexString("9ac312a7f877468ebe69422748ad00a15443f18203a07d6060f688f30f21632b");
        var key = new byte[32];
        var iv = new byte[12];
        var hp = new byte[32];
        var ku = new byte[32];
        QuicInitial.DeriveTraffic(HashAlgorithmName.SHA256, secret, key, iv, hp);
        QuicInitial.DeriveUpdate(HashAlgorithmName.SHA256, secret, ku);
        CollectionAssert.AreEqual(Convert.FromHexString("c6d98ff3441c3fe1b2182094f69caa2ed4b716b65488960a7a984979fb23e1c8"), key);
        CollectionAssert.AreEqual(Convert.FromHexString("e0459b3474bdd0e44a41c144"), iv);
        CollectionAssert.AreEqual(Convert.FromHexString("25a282b9e82f06f21f488917a4fc8f1b73573685608597d0efcb076b0ab7a7a4"), hp);
        CollectionAssert.AreEqual(Convert.FromHexString("1223504755036d556342ee9361d253421a826c9ecdf3c7148684b36b714881f9"), ku);

        byte[] header = Convert.FromHexString("4200bff4");
        byte[] sample = Convert.FromHexString("5e5cd55c41f69080575d7999c25a5bfb");
        using var protection = new QuicHeaderProtection(QuicHeaderCipher.ChaCha20, hp);
        Assert.IsTrue(protection.Protect(header, packetNumberOffset: 1, sample));
        CollectionAssert.AreEqual(Convert.FromHexString("4cfe4189"), header);
        Assert.IsTrue(protection.Remove(header, packetNumberOffset: 1, sample));
        CollectionAssert.AreEqual(Convert.FromHexString("4200bff4"), header);
    }

    private static void AssertSide(bool client, string secret, string key, string iv, string hp)
    {
        var gotSecret = new byte[32];
        var gotKey = new byte[16];
        var gotIv = new byte[12];
        var gotHp = new byte[16];
        QuicInitial.Derive(ConnectionId, client, gotSecret, gotKey, gotIv, gotHp);
        CollectionAssert.AreEqual(Convert.FromHexString(secret), gotSecret);
        CollectionAssert.AreEqual(Convert.FromHexString(key), gotKey);
        CollectionAssert.AreEqual(Convert.FromHexString(iv), gotIv);
        CollectionAssert.AreEqual(Convert.FromHexString(hp), gotHp);
    }

    private static void AssertHeader(int packetNumberOffset, string unprotected, string sample, string protectedHeader, string headerKey)
    {
        byte[] header = Convert.FromHexString(unprotected);
        byte[] original = (byte[])header.Clone();
        using var protection = new QuicHeaderProtection(QuicHeaderCipher.Aes128, Convert.FromHexString(headerKey));
        Assert.IsTrue(protection.Protect(header, packetNumberOffset, Convert.FromHexString(sample)));
        CollectionAssert.AreEqual(Convert.FromHexString(protectedHeader), header);
        Assert.IsTrue(protection.Remove(header, packetNumberOffset, Convert.FromHexString(sample)));
        CollectionAssert.AreEqual(original, header);
    }
}
