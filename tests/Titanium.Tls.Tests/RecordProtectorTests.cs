using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class RecordProtectorTests
{    private const string ServerFlightPlain = "080000240022000a00140012001d00170018001901000101010201030104001c00024001000000000b0001b9000001b50001b0308201ac30820115a003020102020102300d06092a864886f70d01010b0500300e310c300a06035504031303727361301e170d3136303733303031323335395a170d3236303733303031323335395a300e310c300a0603550403130372736130819f300d06092a864886f70d010101050003818d0030818902818100b4bb498f8279303d980836399b36c6988c0c68de55e1bdb826d3901a2461eafd2de49a91d015abbc9a95137ace6c1af19eaa6af98c7ced43120998e187a80ee0ccb0524b1b018c3e0b63264d449a6d38e22a5fda430846748030530ef0461c8ca9d9efbfae8ea6d1d03e2bd193eff0ab9a8002c47428a6d35a8d88d79f7f1e3f0203010001a31a301830090603551d1304023000300b0603551d0f0404030205a0300d06092a864886f70d01010b05000381810085aad2a0e5b9276b908c65f73a7267170618a54c5f8a7b337d2df7a594365417f2eae8f8a58c8f8172f9319cf36b7fd6c55b80f21a03015156726096fd335e5e67f2dbf102702e608ccae6bec1fc63a42a99be5c3eb7107c3c54e9b9eb2bd5203b1c3b84e0a8b2f759409ba3eac9d91d402dcc0cc8f8961229ac9187b42b4de100000f000084080400805a747c5d88fa9bd2e55ab085a61015b7211f824cd484145ab3ff52f1fda8477b0b7abc90db78e2d33a5c141a078653fa6bef780c5ea248eeaaa785c4f394cab6d30bbe8d4859ee511f602957b15411ac027671459e46445c9ea58c181e818e95b8c3fb0bf3278409d3be152a3da5043e063dda65cdf5aea20d53dfacd42f74f3140000209b9b141d906337fbd2cbdce71df4deda4ab42c309572cb7fffee5454b78f0718";
    private const string ServerFlightRecord = "17030302a2d1ff334a56f5bff6594a07cc87b580233f500f45e489e7f33af35edf7869fcf40aa40aa2b8ea73f848a7ca07612ef9f945cb960b4068905123ea78b111b429ba9191cd05d2a389280f526134aadc7fc78c4b729df828b5ecf7b13bd9aefb0e57f271585b8ea9bb355c7c79020716cfb9b1183ef3ab20e37d57a6b9d7477609aee6e122a4cf51427325250c7d0e509289444c9b3a648f1d71035d2ed65b0e3cdd0cbae8bf2d0b227812cbb360987255cc744110c453baa4fcd610928d809810e4b7ed1a8fd991f06aa6248204797e36a6a73b70a2559c09ead686945ba246ab66e5edd8044b4c6de3fcf2a89441ac66272fd8fb330ef8190579b3684596c960bd596eea520a56a8d650f563aad27409960dca63d3e688611ea5e22f4415cf9538d51a200c27034272968a264ed6540c84838d89f72c24461aad6d26f59ecaba9acbbb317b66d902f4f292a36ac1b639c637ce343117b659622245317b49eeda0c6258f100d7d961ffb138647e92ea330faeea6dfa31c7a84dc3bd7e1b7a6c7178af36879018e3f252107f243d243dc7339d5684c8b0378bf30244da8c87c843f5e56eb4c5e8280a2b48052cf93b16499a66db7cca71e4599426f7d461e66f99882bd89fc50800becca62d6c74116dbd2972fda1fa80f85df881edbe5a37668936b335583b599186dc5c6918a396fa48a181d6b6fa4f9d62d513afbb992f2b992f67f8afe67f76913fa388cb5630c8ca01e0c65d11c66a1e2ac4c85977b7c7a6999bbf10dc35ae69f5515614636c0b9b68c19ed2e31c0b3b66763038ebba42f3b38edc0399f3a9f23faa63978c317fc9fa66a73f60f0504de93b5b845e275592c12335ee340bbc4fddd502784016e4b3be7ef04dda49f4b440a30cb5d2af939828fd4ae3794e44f94df5a631ede42c1719bfdabf0253fe5175be898e750edc53370d2b";
    private const string ClientFinishedPlain = "14000020a8ec436d677634ae525ac1fcebe11a039ec17694fac6e98527b642f2edd5ce61";
    private const string ClientFinishedRecord = "170303003575ec4dc238cce60b298044a71e219c56cc77b0517fe9b93c7a4bfc44d87f38f80338ac98fc46deb384bd1caeacab6867d726c40546";

    [TestMethod]
    public void UnprotectsRfc8448ServerHandshakeFlight()
    {
        byte[] record = Hex(ServerFlightRecord);
        using var protector = new TlsRecordProtector(
            TlsAeadSuite.Aes128Gcm,
            Hex("3fce516009c21727d0f2e4e86ee403bc"),
            Hex("5d313eb2671276ee13000b30"));
        byte type = protector.Unprotect(record, out int length);
        Assert.AreEqual(22, type);
        Assert.IsTrue(record.AsSpan(5, length).SequenceEqual(Hex(ServerFlightPlain)));
    }

    [TestMethod]
    public void UnprotectsRfc8448ClientFinished()
    {
        byte[] record = Hex(ClientFinishedRecord);
        using var protector = new TlsRecordProtector(
            TlsAeadSuite.Aes128Gcm,
            Hex("dbfaa693d1762c5b666af5d950258d01"),
            Hex("5bd3c71b836e0b76bb73265f"));
        byte type = protector.Unprotect(record, out int length);
        Assert.AreEqual(22, type);
        Assert.IsTrue(record.AsSpan(5, length).SequenceEqual(Hex(ClientFinishedPlain)));
    }

    [TestMethod]
    public void RoundTripsAesAndChaChaWithPadding()
    {
        RoundTrip(TlsAeadSuite.Aes128Gcm, 16);
        RoundTrip(TlsAeadSuite.Aes256Gcm, 32);
        RoundTrip(TlsAeadSuite.ChaCha20Poly1305, 32);
    }

    [TestMethod]
    [RfcRequirement("RFC 8446", "5", "RecordProtectorTests.RejectsTamperReplayOverflowAndEmptyInner")]
    public void RejectsTamperReplayOverflowAndEmptyInner()
    {
        byte[] key = new byte[16];
        byte[] iv = new byte[12];
        key[0] = 1;
        iv[0] = 2;
        using var write = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv, keyUpdateAfter: 1);
        byte[] plain = [1, 2, 3, 4];
        byte[] record = new byte[64];
        int written = write.Protect(22, plain, record);
        Assert.IsTrue(write.KeyUpdateDue);

        byte[] opened = record[..written];
        using var read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        byte type = read.Unprotect(opened, out int length);
        Assert.AreEqual(22, type);
        Assert.IsTrue(opened.AsSpan(5, length).SequenceEqual(plain));
        TlsRecordException replay = Assert.ThrowsException<TlsRecordException>(() => read.Unprotect(opened, out _));
        Assert.AreEqual(TlsRecordAlert.BadRecordMac, replay.Alert);

        byte[] tampered = record[..written];
        tampered[^1] ^= 1;
        using var read2 = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        TlsRecordException badMac = Assert.ThrowsException<TlsRecordException>(() => read2.Unprotect(tampered, out _));
        Assert.AreEqual(TlsRecordAlert.BadRecordMac, badMac.Alert);

        byte[] huge = new byte[TlsRecordProtector.MaxPlaintext + 1];
        TlsRecordException overflow = Assert.ThrowsException<TlsRecordException>(() => write.Protect(23, huge, new byte[huge.Length + 64]));
        Assert.AreEqual(TlsRecordAlert.RecordOverflow, overflow.Alert);

        byte[] shortRecord = [23, 3, 3, 0, 0];
        TlsRecordException empty = Assert.ThrowsException<TlsRecordException>(() => read2.Unprotect(shortRecord, out _));
        Assert.AreEqual(TlsRecordAlert.UnexpectedMessage, empty.Alert);

        Assert.IsFalse(TlsRecordProtector.TryReadInner(new byte[8], out _, out _));
        Assert.IsTrue(TlsRecordProtector.TryReadInner([9, 22, 0, 0], out byte innerType, out int innerLength));
        Assert.AreEqual(22, innerType);
        Assert.AreEqual(1, innerLength);
    }

    [TestMethod]
    public void DisposeZeroesTheIv()
    {
        byte[] iv = new byte[12];
        iv[3] = 9;
        var protector = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, new byte[16], iv);
        Assert.IsFalse(protector.IvIsZeroed);
        protector.Dispose();
        Assert.IsTrue(protector.IvIsZeroed);
    }

    [TestMethod]
    public void ProtectAddsNoAllocationBeyondTheAead()
    {
        byte[] key = new byte[16];
        byte[] iv = new byte[12];
        byte[] plain = new byte[32];
        using var protector = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        byte[] destination = new byte[128];
        protector.Protect(23, plain, destination);
        using var aes = new AesGcm(key, 16);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> cipher = stackalloc byte[32];
        Span<byte> tag = stackalloc byte[16];
        aes.Encrypt(nonce, plain, cipher, tag);

        const int iterations = 32;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        long aeadOnly = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            protector.Protect(23, plain, destination);
        }

        long protect = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(protect <= aeadOnly);
    }

    private static void RoundTrip(TlsAeadSuite suite, int keyLength)
    {
        byte[] key = new byte[keyLength];
        byte[] iv = new byte[12];
        key[1] = 7;
        iv[2] = 8;
        byte[] plain = [10, 20, 30, 40, 50];
        using var write = new TlsRecordProtector(suite, key, iv);
        using var read = new TlsRecordProtector(suite, key, iv);
        byte[] record = new byte[64];
        int written = write.Protect(23, plain, record, padding: 3);
        byte[] opened = record[..written];
        byte type = read.Unprotect(opened, out int length);
        Assert.AreEqual(23, type);
        Assert.IsTrue(opened.AsSpan(5, length).SequenceEqual(plain));
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