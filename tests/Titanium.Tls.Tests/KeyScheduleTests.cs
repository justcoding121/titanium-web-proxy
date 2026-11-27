using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

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
    [RfcRequirement("RFC 8446", "7", "KeyScheduleTests.MasterTrafficKeysAndResumptionMatchRfc8448Section3")]
    public void MasterTrafficKeysAndResumptionMatchRfc8448Section3()
    {
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly(ReadOnlySpan<byte>.Empty);
        schedule.AdvanceToHandshake(Hex("8bd4054fb55b9d63fdfbacf9f04b9f0d35e6d63f537563efd46272900f89492d"));
        byte[] hsTranscript = Hex("860c06edc07858ee8e78f0e7428c58edd6b43f2ca3e6e95f02ed063cf0e1cad8");
        Span<byte> serverHs = stackalloc byte[32];
        schedule.HandshakeTrafficSecret(client: false, hsTranscript, serverHs);
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverHs, key, iv);
        AssertEqual(key, "3fce516009c21727d0f2e4e86ee403bc");
        AssertEqual(iv, "5d313eb2671276ee13000b30");
        Span<byte> finished = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, serverHs, finished);
        AssertEqual(finished, "008d3b66f816ea559f96b537e885c31fc068bf492c652f01f288a1d8cdc19fc8");

        schedule.AdvanceToMaster();
        Assert.IsTrue(schedule.IsStageZeroed(KeyScheduleStage.Early));
        Assert.IsTrue(schedule.IsStageZeroed(KeyScheduleStage.Handshake));
        Span<byte> master = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Master, master);
        AssertEqual(master, "18df06843d13a08bf2a449844c5f8a478001bc4d4c627984d5a41da8d0402919");

        byte[] apTranscript = Hex("9608102a0f1ccc6db6250b7b7e417b1a000eaada3daae4777a7686c9ff83df13");
        Span<byte> clientAp = stackalloc byte[32];
        Span<byte> serverAp = stackalloc byte[32];
        Span<byte> exporter = stackalloc byte[32];
        schedule.ApplicationTrafficSecret(client: true, apTranscript, clientAp);
        schedule.ApplicationTrafficSecret(client: false, apTranscript, serverAp);
        schedule.ExporterMaster(apTranscript, exporter);
        AssertEqual(clientAp, "9e40646ce79a7f9dc05af8889bce6552875afa0b06df0087f792ebb7c17504a5");
        AssertEqual(serverAp, "a11af9f05531f856ad47116b45a950328204b4f44bfb6b3a4b4f1f3fcb631643");
        AssertEqual(exporter, "fe22f881176eda18eb8f44529e6792c50c9a3f89452f68d8ae311b4309d3cf50");
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverAp, key, iv);
        AssertEqual(key, "9f02283b6c9c07efc26bb9f2ac92e356");
        AssertEqual(iv, "cf782b88dd83549aadf1e984");

        byte[] resTranscript = Hex("209145a96ee8e2a122ff810047cc952684658d6049e86429426db87c54ad143d");
        Span<byte> resumptionMaster = stackalloc byte[32];
        schedule.ResumptionMaster(resTranscript, resumptionMaster);
        AssertEqual(resumptionMaster, "7df235f2031d2a051287d02b0241b0bfdaf86cc856231f2d5aba46c434ec196c");
        Span<byte> psk = stackalloc byte[32];
        TlsKeySchedule.ResumptionPsk(HashAlgorithmName.SHA256, resumptionMaster, [0, 0], psk);
        AssertEqual(psk, "4ecd0eb6ec3b4d87f5d6028f922ca4c5851a277fd41311c9e62d2c9492e1c4f3");
    }

    [TestMethod]
    public void ResumedEarlySecretsMatchRfc8448Section4()
    {
        byte[] psk = Hex("4ecd0eb6ec3b4d87f5d6028f922ca4c5851a277fd41311c9e62d2c9492e1c4f3");
        byte[] earlyTranscript = Hex("08ad0fa05d7c7233b1775ba2ff9f4c5b8b59276b7f227f13a976245f5d960913");
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly(psk);
        Span<byte> early = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Early, early);
        AssertEqual(early, "9b2188e9b2fc6d64d71dc329900e20bb41915000f678aa839cbb797cb7d8332c");

        Span<byte> binder = stackalloc byte[32];
        Span<byte> emptyHash = stackalloc byte[32];
        SHA256.HashData(ReadOnlySpan<byte>.Empty, emptyHash);
        schedule.DeriveEarly("res binder"u8, emptyHash, binder);
        AssertEqual(binder, "69fe131a3bbad5d63c64eebcc30e395b9d8107726a13d074e389dbc8a4e47256");
        Span<byte> finished = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, binder, finished);
        AssertEqual(finished, "5588673e72cb59c87d220caffe94f2dea9a3b1609f7d50e90a48227db9ed7eaa");

        Span<byte> earlyTraffic = stackalloc byte[32];
        Span<byte> earlyExporter = stackalloc byte[32];
        schedule.DeriveEarly("c e traffic"u8, earlyTranscript, earlyTraffic);
        schedule.DeriveEarly("e exp master"u8, earlyTranscript, earlyExporter);
        AssertEqual(earlyTraffic, "3fbbe6a60deb66c30a32795aba0eff7eaa10105586e7be5c09678d63b6caab62");
        AssertEqual(earlyExporter, "b2026866610937d7423e5be90862ccf24c0e6091186d34f812089ff5be2ef7df");

        schedule.AdvanceToHandshake(Hex("f44194756ff9ec9d25180635d66ea6824c6ab3bf179977be37f723570e7ccb2e"));
        Span<byte> handshake = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Handshake, handshake);
        AssertEqual(handshake, "005cb112fd8eb4ccc623bb88a07c64b3ede1605363fc7d0df8c7ce4ff0fb4ae6");
        schedule.AdvanceToMaster();
        Span<byte> master = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Master, master);
        AssertEqual(master, "e2d32d4ed66dd37897a0e80c84107503ce58bf8aad4cb55a5002d77ecb890ece");
    }

    [TestMethod]
    public void HelloRetryRequestSecretsMatchRfc8448Section5()
    {
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly(ReadOnlySpan<byte>.Empty);
        schedule.AdvanceToHandshake(Hex("c142ce13ca11b5c2233652e63ad3d97844f1621fbfb9de69d547dc8fedeabeb4"));
        Span<byte> handshake = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Handshake, handshake);
        AssertEqual(handshake, "ce022e5e6e81e50736d773f2d3adfce8220d049bf510f0dbfac927ef4243b148");
        byte[] transcript = Hex("8aa8e828ec2f8a884fec95a3139de01c15a3daa7ff5bfc3f4bfcc21b438d7bf8");
        Span<byte> client = stackalloc byte[32];
        schedule.HandshakeTrafficSecret(client: true, transcript, client);
        AssertEqual(client, "158aa7ab8855073582b41d674b4055cabcc534728f659314861b4e08e2011566");
        schedule.AdvanceToMaster();
        Span<byte> master = stackalloc byte[32];
        schedule.CopyStageSecret(KeyScheduleStage.Master, master);
        AssertEqual(master, "1131545d0baf79ddce9b87f06945781a57dd18ef378dcd2060f8f9a569027ed8");
    }

    [TestMethod]
    public void DisposeZeroesEveryStageSecret()
    {
        var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly(Hex("4ecd0eb6ec3b4d87f5d6028f922ca4c5851a277fd41311c9e62d2c9492e1c4f3"));
        Assert.IsFalse(schedule.IsStageZeroed(KeyScheduleStage.Early));
        schedule.Dispose();
        Assert.IsTrue(schedule.IsStageZeroed(KeyScheduleStage.Early));
        Assert.IsTrue(schedule.IsStageZeroed(KeyScheduleStage.Handshake));
        Assert.IsTrue(schedule.IsStageZeroed(KeyScheduleStage.Master));
    }

    [TestMethod]
    public void Sha384ScheduleMatchesIndependentHmac()
    {
        byte[] psk = new byte[48];
        byte[] dhe = new byte[32];
        byte[] transcript = new byte[48];
        Array.Fill(psk, (byte)0x11);
        Array.Fill(dhe, (byte)0x22);
        Array.Fill(transcript, (byte)0x33);
        byte[] emptyHash = SHA384.HashData([]);

        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA384);
        schedule.InitializeEarly(psk);
        Span<byte> early = stackalloc byte[48];
        schedule.CopyStageSecret(KeyScheduleStage.Early, early);
        Assert.IsTrue(early.SequenceEqual(ReferenceExtract(HashAlgorithmName.SHA384, psk, new byte[48])));

        schedule.AdvanceToHandshake(dhe);
        byte[] derived = ReferenceExpandLabel(HashAlgorithmName.SHA384, early.ToArray(), "derived", emptyHash, 48);
        Span<byte> handshake = stackalloc byte[48];
        schedule.CopyStageSecret(KeyScheduleStage.Handshake, handshake);
        Assert.IsTrue(handshake.SequenceEqual(ReferenceExtract(HashAlgorithmName.SHA384, dhe, derived)));

        Span<byte> traffic = stackalloc byte[48];
        schedule.HandshakeTrafficSecret(client: true, transcript, traffic);
        Assert.IsTrue(traffic.SequenceEqual(ReferenceExpandLabel(HashAlgorithmName.SHA384, handshake.ToArray(), "c hs traffic", transcript, 48)));

        Span<byte> key = stackalloc byte[32];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA384, traffic, key, iv);
        Assert.IsTrue(key.SequenceEqual(ReferenceExpandLabel(HashAlgorithmName.SHA384, traffic.ToArray(), "key", [], 32)));
        Assert.IsTrue(iv.SequenceEqual(ReferenceExpandLabel(HashAlgorithmName.SHA384, traffic.ToArray(), "iv", [], 12)));

        schedule.AdvanceToMaster();
        byte[] derivedMaster = ReferenceExpandLabel(HashAlgorithmName.SHA384, handshake.ToArray(), "derived", emptyHash, 48);
        Span<byte> master = stackalloc byte[48];
        schedule.CopyStageSecret(KeyScheduleStage.Master, master);
        Assert.IsTrue(master.SequenceEqual(ReferenceExtract(HashAlgorithmName.SHA384, new byte[48], derivedMaster)));
    }

    [TestMethod]
    public void ExporterAndKeyUpdateMatchIndependentHmac()
    {
        byte[] exporterMaster = Hex("fe22f881176eda18eb8f44529e6792c50c9a3f89452f68d8ae311b4309d3cf50");
        byte[] label = "exp"u8.ToArray();
        byte[] context = "ctx"u8.ToArray();
        Span<byte> exported = stackalloc byte[20];
        TlsKeySchedule.ExportKeyingMaterial(HashAlgorithmName.SHA256, exporterMaster, label, context, exported);
        byte[] emptyHash = SHA256.HashData([]);
        byte[] derived = ReferenceExpandLabel(HashAlgorithmName.SHA256, exporterMaster, "exp", emptyHash, 32);
        byte[] contextHash = SHA256.HashData(context);
        Assert.IsTrue(exported.SequenceEqual(ReferenceExpandLabel(HashAlgorithmName.SHA256, derived, "exporter", contextHash, 20)));

        byte[] traffic = Hex("b3eddb126e067f35a780b3abf45e2d8f3b1a950738f52e9600746a0e27a55a21");
        Span<byte> next = stackalloc byte[32];
        TlsKeySchedule.NextTrafficSecret(HashAlgorithmName.SHA256, traffic, next);
        Assert.IsTrue(next.SequenceEqual(ReferenceExpandLabel(HashAlgorithmName.SHA256, traffic, "traffic upd", [], 32)));
    }

    [TestMethod]
    public void OutOfOrderAdvanceThrows()
    {
        using var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        Assert.ThrowsException<InvalidOperationException>(() => schedule.AdvanceToHandshake(new byte[32]));
        schedule.InitializeEarly(ReadOnlySpan<byte>.Empty);
        Assert.ThrowsException<InvalidOperationException>(() => schedule.AdvanceToMaster());
    }

    private static void AssertEqual(ReadOnlySpan<byte> actual, string expectedHex)
    {
        Assert.IsTrue(actual.SequenceEqual(Hex(expectedHex)));
    }

    private static byte[] ReferenceExtract(HashAlgorithmName hash, byte[] ikm, byte[] salt)
    {
        using HMAC hmac = hash == HashAlgorithmName.SHA384 ? new HMACSHA384(salt) : new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] ReferenceExpandLabel(HashAlgorithmName hash, byte[] secret, string label, byte[] context, int length)
    {
        byte[] prefix = "tls13 "u8.ToArray();
        byte[] labelBytes = Encoding.ASCII.GetBytes(label);
        byte[] info = new byte[2 + 1 + prefix.Length + labelBytes.Length + 1 + context.Length];
        info[0] = (byte)(length >> 8);
        info[1] = (byte)length;
        info[2] = (byte)(prefix.Length + labelBytes.Length);
        prefix.CopyTo(info, 3);
        labelBytes.CopyTo(info, 3 + prefix.Length);
        int contextAt = 3 + prefix.Length + labelBytes.Length;
        info[contextAt] = (byte)context.Length;
        context.CopyTo(info, contextAt + 1);
        return ReferenceExpand(hash, secret, info, length);
    }

    private static byte[] ReferenceExpand(HashAlgorithmName hash, byte[] prk, byte[] info, int length)
    {
        int hashLength = hash == HashAlgorithmName.SHA384 ? 48 : 32;
        byte[] okm = new byte[length];
        byte[] previous = [];
        int offset = 0;
        for (int counter = 1; offset < length; counter++)
        {
            using HMAC hmac = hash == HashAlgorithmName.SHA384 ? new HMACSHA384(prk) : new HMACSHA256(prk);
            byte[] input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input, previous.Length);
            input[^1] = (byte)counter;
            previous = hmac.ComputeHash(input);
            int take = Math.Min(hashLength, length - offset);
            previous.AsSpan(0, take).CopyTo(okm.AsSpan(offset));
            offset += take;
        }

        return okm;
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
