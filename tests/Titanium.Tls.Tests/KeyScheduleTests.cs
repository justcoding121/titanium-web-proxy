using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class KeyScheduleTests
{
    [TestMethod]
    public void EarlyAndHandshakeSecretsMatchRfc8448Section3()
    {
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly(ReadOnlySpan<byte>.Empty);
        Span<byte> early = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Early, early);
        Assert.IsTrue(early.SequenceEqual(Hex("33ad0a1c607ec03b09e6cd9893680ce210adf300aa1f2660e1b22e10f170f92a")));

        schedule.AdvanceToHandshake(Hex("8bd4054fb55b9d63fdfbacf9f04b9f0d35e6d63f537563efd46272900f89492d"));
        Span<byte> handshake = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Handshake, handshake);
        Assert.IsTrue(handshake.SequenceEqual(Hex("1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac")));
        Assert.IsTrue(early.ToArray().Length == 32);
        Span<byte> earlyAfter = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Early, earlyAfter);
        Assert.IsTrue(earlyAfter.SequenceEqual(new byte[32]));

        byte[] transcript = Hex("860c06edc07858ee8e78f0e7428c58edd6b43f2ca3e6e95f02ed063cf0e1cad8");
        Span<byte> client = stackalloc byte[32];
        Span<byte> server = stackalloc byte[32];
        schedule.HandshakeTrafficSecret(client: true, transcript, client);
        schedule.HandshakeTrafficSecret(client: false, transcript, server);
        Assert.IsTrue(client.SequenceEqual(Hex("b3eddb126e067f35a780b3abf45e2d8f3b1a950738f52e9600746a0e27a55a21")));
        Assert.IsTrue(server.SequenceEqual(Hex("b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38")));
    }

    [TestMethod]
    public void OutOfOrderAdvanceThrows()
    {
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        Assert.ThrowsException<InvalidOperationException>(() => schedule.AdvanceToHandshake(new byte[32]));
        schedule.InitializeEarly(ReadOnlySpan<byte>.Empty);
        Assert.ThrowsException<InvalidOperationException>(() => schedule.AdvanceToMaster());
    }

    private static byte[] Hex(string text)
    {
        byte[] bytes = new byte[text.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}
