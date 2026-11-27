using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class FingerprintProfileTests
{
    [TestMethod]
    public void Ja3AndJa4MatchThePublishedExamples()
    {
        Assert.AreEqual("ada70206e40642a3e4461f35503241d5", Ja3.Hash("769,47-53-5-10-49161-49162-49171-49172-50-56-19-4,0-10-11,23-24-25,0"));
        Assert.AreEqual("de350869b8c85de67a350c8d186f11e6", Ja3.Hash("769,4-5-10-9-100-98-3-6-19-18-99,,,"));
        Assert.AreEqual("8daaf6152771", Ja4.CipherHash("002f,0035,009c,009d,1301,1302,1303,c013,c014,c02b,c02c,c02f,c030,cca8,cca9"));
        Assert.AreEqual("e5627efa2ab1", Ja4.ExtensionHash("0005,000a,000b,000d,0012,0015,0017,001b,0023,002b,002d,0033,4469,ff01_0403,0804,0401,0503,0805,0501,0806,0601"));
        Assert.AreEqual("6d807ffa2a79", Ja4.ExtensionHash("0005,000a,000b,000d,0012,0015,0017,001b,0023,002b,002d,0033,4469,ff01"));
    }

    [TestMethod]
    public void GeneratedChromeHellosMatchTheCapturedShape()
    {
        byte[] captured = File.ReadAllBytes(Find("hello-001.bin"));
        string ja4 = Ja4.Fingerprint(captured);
        string ja3Fields = Ja3.Describe(captured);
        var profile = new ChromeFingerprintProfile(new SystemEntropy(), TlsFingerprintFidelity.Full);
        var seen = new int[17, 17];
        var middle = new HashSet<ushort>();
        for (int i = 0; i < 1000; i++)
        {
            byte[] hello = Build(profile, ChromeContext());
            Assert.AreEqual(ja4, Ja4.Fingerprint(hello), i.ToString());
            AssertSameSets(ja3Fields, Ja3.Describe(hello));
            ushort[] types = ExtensionTypes(hello);
            Assert.AreEqual(19, types.Length);
            Assert.IsTrue(Grease.IsGrease(types[0]));
            Assert.IsTrue(Grease.IsGrease(types[^1]));
            Assert.AreNotEqual(types[0], types[^1]);
            AssertSameGrease(hello);
            for (int slot = 0; slot < 17; slot++)
            {
                middle.Add(types[slot + 1]);
                seen[Index(types[slot + 1]), slot]++;
            }
        }

        Assert.AreEqual(17, middle.Count);
        for (int extension = 0; extension < 17; extension++)
        {
            for (int slot = 0; slot < 17; slot++)
            {
                Assert.IsTrue(seen[extension, slot] > 0, extension + ":" + slot);
            }
        }
    }

    [TestMethod]
    public void HelloRetryRequestKeepsExtensionOrder()
    {
        var profile = new ChromeFingerprintProfile(new CountingEntropy(), TlsFingerprintFidelity.Full);
        ClientHelloContext firstContext = ChromeContext();
        byte[] first = Build(profile, firstContext);
        var secondContext = ChromeContext();
        secondContext.HelloRetryState = firstContext.HelloRetryState;
        byte[] second = Build(profile, secondContext);
        CollectionAssert.AreEqual(ExtensionTypes(first), ExtensionTypes(second));
        Assert.IsFalse(first.AsSpan(11, 32).SequenceEqual(second.AsSpan(11, 32)));
    }

    [TestMethod]
    public void ResumedHelloPutsPreSharedKeyLast()
    {
        var profile = new ChromeFingerprintProfile(new CountingEntropy(), TlsFingerprintFidelity.Full);
        byte[] hello = Build(profile, ChromeContext(resume: true));
        ushort[] types = ExtensionTypes(hello);
        Assert.AreEqual(ExtensionType.PreSharedKey, types[^1]);
        Assert.IsTrue(Grease.IsGrease(types[^2]));
    }

    [TestMethod]
    public void ReducedFidelityOmitsTheHybridShare()
    {
        var profile = new ChromeFingerprintProfile(new CountingEntropy(), TlsFingerprintFidelity.ReducedNoPostQuantum);
        byte[] hello = Build(profile, new ClientHelloContext
        {
            ServerName = "localhost",
            KeyShares = new Dictionary<ushort, byte[]> { [0x001D] = new byte[32] },
        });
        Assert.IsFalse(JaHello.ReadList16(ExtensionBody(hello, ExtensionType.SupportedGroups)).Contains((ushort)0x11EC));
        Assert.IsFalse(ExtensionBody(hello, ExtensionType.KeyShare).IndexOf(new byte[] { 0x11, 0xEC }) >= 0);
        Assert.IsTrue(JaHello.ReadList16(ExtensionBody(hello, ExtensionType.SupportedGroups)).Contains((ushort)0x001D));
    }

    [TestMethod]
    public void SyntheticTemplatesCoverOrderGreasePaddingAndRecords()
    {
        var fixedProfile = new TemplateFingerprintProfile(Sample(ExtensionOrderPolicy.Fixed), new CountingEntropy());
        byte[] left = Build(fixedProfile, new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        byte[] right = Build(new TemplateFingerprintProfile(Sample(ExtensionOrderPolicy.Fixed), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        CollectionAssert.AreEqual(ExtensionTypes(left), ExtensionTypes(right));

        var grouped = new TemplateFingerprintProfile(Grouped(), new CountingEntropy());
        bool swapped = false;
        for (int i = 0; i < 30; i++)
        {
            ushort[] types = ExtensionTypes(Build(grouped, new ClientHelloContext { KeyShares = Shares(0x001D, 32) }));
            Assert.IsTrue(types[0] is 1 or 2);
            Assert.IsTrue(types[1] is 1 or 2);
            Assert.IsTrue(types[2] is 3 or 4);
            Assert.IsTrue(types[3] is 3 or 4);
            swapped |= types[0] == 2;
        }

        Assert.IsTrue(swapped);
        byte[] plain = Build(new TemplateFingerprintProfile(Sample(grease: false), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        foreach (ushort extension in ExtensionTypes(plain))
        {
            Assert.IsFalse(Grease.IsGrease(extension));
        }

        byte[] padded = Build(new TemplateFingerprintProfile(Sample(padding: PaddingPolicy.PadToLength, target: 200), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        Assert.IsTrue(padded.Length >= 200);
        Assert.IsTrue(ExtensionTypes(padded).Contains(ExtensionType.Padding));
        byte[] unpadded = Build(new TemplateFingerprintProfile(Sample(padding: PaddingPolicy.PadWithinBand, target: 200, bandMin: 400, bandMax: 500), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        Assert.IsFalse(ExtensionTypes(unpadded).Contains(ExtensionType.Padding));

        byte[] split = Build(new TemplateFingerprintProfile(Sample(split: true), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32) });
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(split, out ReadOnlySpan<byte> firstRecord, out _, out _));
        ReadOnlySpan<byte> rest = split.AsSpan(5 + firstRecord.Length);
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(rest, out ReadOnlySpan<byte> secondRecord, out _, out _));
        var reassembler = new HandshakeReassembler();
        Assert.IsTrue(reassembler.TryAppend(firstRecord, out _));
        Assert.IsTrue(reassembler.TryAppend(secondRecord, out _));
        var body = new byte[split.Length];
        Assert.IsTrue(reassembler.TryTake(body, out HandshakeType messageType, out int length, out bool needMore, out _));
        Assert.AreEqual(HandshakeType.ClientHello, messageType);
        Assert.IsFalse(needMore);
        Assert.IsTrue(length > 0);

        byte[] opaque = Build(new TemplateFingerprintProfile(Opaque(), new CountingEntropy()), new ClientHelloContext { KeyShares = Shares(0x001D, 32, 0x0017, 65) });
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x01 }, ExtensionBody(opaque, 28).ToArray());
        Assert.IsTrue(JaHello.ReadList16(ExtensionBody(opaque, ExtensionType.KeyShare)).Count >= 0);
        Assert.IsTrue(ExtensionBody(opaque, ExtensionType.KeyShare).IndexOf(new byte[] { 0x00, 0x1D }) >= 0);
        Assert.IsTrue(ExtensionBody(opaque, ExtensionType.KeyShare).IndexOf(new byte[] { 0x00, 0x17 }) >= 0);
    }

    [TestMethod]
    public void ImplementedTls12CipherIsRejected()
    {
        ClientHelloTemplate template = Sample();
        template.Ciphers[0] = new AdvertisedCode(0xC02B, implemented: true);
        Assert.ThrowsException<TlsProtocolViolation>(() => new TemplateFingerprintProfile(template).BuildClientHello(new ClientHelloContext(), new ArrayBufferWriter<byte>()));
    }

    [TestMethod]
    public void ProductCodeOutsideTheChromeProfileDoesNotNameChrome()
    {
        foreach (string file in Directory.GetFiles(FindSource(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("Fingerprint", "Profiles"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            Assert.IsFalse(text.Contains("Chrome", StringComparison.Ordinal), file);
        }
    }

    private static void AssertSameGrease(byte[] hello)
    {
        ReadOnlySpan<byte> groups = ExtensionBody(hello, ExtensionType.SupportedGroups);
        ReadOnlySpan<byte> shares = ExtensionBody(hello, ExtensionType.KeyShare);
        ushort groupGrease = JaHello.ReadU16(groups, 2);
        ushort shareGrease = JaHello.ReadU16(shares, 2);
        Assert.IsTrue(Grease.IsGrease(shareGrease));
        Assert.AreEqual(groupGrease, shareGrease);
    }

    private static void AssertSameSets(string captured, string generated)
    {
        string[] left = captured.Split(',');
        string[] right = generated.Split(',');
        Assert.IsTrue(SameSet(left[1], right[1]));
        Assert.IsTrue(SameSet(left[3], right[3]));
        Assert.IsTrue(SameSet(left[4], right[4]));
    }

    private static bool SameSet(string left, string right)
    {
        string[] a = left.Length == 0 ? [] : left.Split('-');
        string[] b = right.Length == 0 ? [] : right.Split('-');
        Array.Sort(a, StringComparer.Ordinal);
        Array.Sort(b, StringComparer.Ordinal);
        return a.AsSpan().SequenceEqual(b);
    }

    private static int Index(ushort type)
    {
        ushort[] order =
        [
            ExtensionType.ExtendedMasterSecret, ExtensionType.ApplicationSettings, ExtensionType.ApplicationLayerProtocolNegotiation,
            ExtensionType.SupportedVersions, ExtensionType.KeyShare, ExtensionType.SessionTicket, ExtensionType.SignedCertificateTimestamp,
            ExtensionType.SignatureAlgorithms, ExtensionType.RenegotiationInfo, ExtensionType.ServerName, ExtensionType.PskKeyExchangeModes,
            ExtensionType.CompressCertificate, ExtensionType.StatusRequest, ExtensionType.SupportedGroups, ExtensionType.EncryptedClientHello,
            0xCA34, ExtensionType.EcPointFormats,
        ];
        return Array.IndexOf(order, type);
    }

    private static ClientHelloContext ChromeContext(bool resume = false)
    {
        return new ClientHelloContext
        {
            ServerName = "localhost",
            KeyShares = Shares(0x11EC, 1216, 0x001D, 32),
            SessionTicket = resume ? [1, 2, 3, 4] : null,
            PskBinder = resume ? new byte[32] : null,
        };
    }

    private static Dictionary<ushort, byte[]> Shares(ushort group, int length, ushort group2 = 0, int length2 = 0)
    {
        var shares = new Dictionary<ushort, byte[]> { [group] = new byte[length] };
        if (length2 > 0)
        {
            shares[group2] = new byte[length2];
        }

        return shares;
    }

    private static byte[] Build(TlsFingerprintProfile profile, ClientHelloContext context)
    {
        var writer = new ArrayBufferWriter<byte>();
        profile.BuildClientHello(context, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static ReadOnlySpan<byte> Payload(byte[] record)
    {
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(record, out ReadOnlySpan<byte> payload, out _, out _));
        return payload;
    }

    private static ushort[] ExtensionTypes(byte[] record)
    {
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(Payload(record), out ClientHelloView hello, out HandshakeAlert alert), alert.ToString());
        var types = new List<ushort>();
        JaHello.Walk(hello.Extensions, (type, _) => types.Add(type));
        return types.ToArray();
    }

    private static ReadOnlySpan<byte> ExtensionBody(byte[] record, ushort wanted)
    {
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(Payload(record), out ClientHelloView hello, out _));
        var reader = new TlsReader(hello.Extensions);
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            if (type == wanted)
            {
                return body;
            }
        }

        return default;
    }

    private static ClientHelloTemplate Sample(ExtensionOrderPolicy order = ExtensionOrderPolicy.Fixed, PaddingPolicy padding = PaddingPolicy.None, int target = 0, int bandMin = 0, int bandMax = 0, bool split = false, bool grease = false)
    {
        return new ClientHelloTemplate
        {
            Name = "sample",
            Version = "0",
            Order = order,
            Padding = padding,
            PaddingTarget = target,
            PaddingBandMin = bandMin,
            PaddingBandMax = bandMax,
            SplitAcrossRecords = split,
            ExtensionGreaseValuesDiffer = grease,
            Ciphers = grease ? [new AdvertisedCode(0, grease: true), new AdvertisedCode(0x1301, implemented: true)] : [new AdvertisedCode(0x1301, implemented: true)],
            Groups = [new AdvertisedCode(0x001D, implemented: true)],
            Versions = [new AdvertisedCode(0x0304, implemented: true)],
            SignatureAlgorithms = [],
            KeyShares = [new KeyShareSlot(0x001D, 32)],
            ApplicationProtocols = ["h2"],
            Extensions =
            [
                new ExtensionSpec { Type = ExtensionType.ServerName, Kind = ExtensionKind.ServerName, Implemented = true, Fixed = true },
                new ExtensionSpec { Type = ExtensionType.KeyShare, Kind = ExtensionKind.KeyShare, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.SupportedVersions, Kind = ExtensionKind.Versions, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.SupportedGroups, Kind = ExtensionKind.NamedGroups, Implemented = true },
                new ExtensionSpec { Type = ExtensionType.Padding, Kind = ExtensionKind.Padding, Implemented = true },
            ],
        };
    }

    private static ClientHelloTemplate Grouped()
    {
        ClientHelloTemplate template = Sample(ExtensionOrderPolicy.Groups);
        template.Extensions =
        [
            new ExtensionSpec { Type = 1, Kind = ExtensionKind.Empty, PermuteGroup = 0 },
            new ExtensionSpec { Type = 2, Kind = ExtensionKind.Empty, PermuteGroup = 0 },
            new ExtensionSpec { Type = 3, Kind = ExtensionKind.Empty, PermuteGroup = 1 },
            new ExtensionSpec { Type = 4, Kind = ExtensionKind.Empty, PermuteGroup = 1 },
            new ExtensionSpec { Type = ExtensionType.KeyShare, Kind = ExtensionKind.KeyShare, Implemented = true, Fixed = true },
            new ExtensionSpec { Type = ExtensionType.SupportedVersions, Kind = ExtensionKind.Versions, Implemented = true, Fixed = true },
        ];
        return template;
    }

    private static ClientHelloTemplate Opaque()
    {
        ClientHelloTemplate template = Sample();
        template.KeyShares = [new KeyShareSlot(0x001D, 32), new KeyShareSlot(0x0017, 65)];
        template.Extensions =
        [
            new ExtensionSpec { Type = 28, Kind = ExtensionKind.Constant, ConstantBody = [0x00, 0x01] },
            new ExtensionSpec { Type = ExtensionType.KeyShare, Kind = ExtensionKind.KeyShare, Implemented = true },
            new ExtensionSpec { Type = ExtensionType.SupportedVersions, Kind = ExtensionKind.Versions, Implemented = true },
        ];
        return template;
    }

    private static string Find(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Titanium.Tls.Tests", "Fixtures", "captures", "chrome", "158.0.8127.89", "tcp", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(name);
    }

    private static string FindSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Titanium.Tls");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Titanium.Tls");
    }
}

[TestClass]
public sealed class CertificateCompressionTests
{
    [TestMethod]
    public void BrotliRoundTripRejectsAMismatchAndATruncatedInput()
    {
        byte[] plain = Encoding.ASCII.GetBytes("certificate-bytes");
        var compressed = new byte[64];
        Assert.IsTrue(BrotliEncoder.TryCompress(plain, compressed, out int written));
        compressed = compressed[..written];
        var output = new byte[plain.Length];
        Assert.IsTrue(CertificateCompression.TryDecompress(CertificateCompression.Brotli, compressed, plain.Length, 131072, output, out int decoded, out _));
        Assert.AreEqual(plain.Length, decoded);
        CollectionAssert.AreEqual(plain, output);
        Assert.IsFalse(CertificateCompression.TryDecompress(CertificateCompression.Brotli, compressed, plain.Length + 1, 131072, new byte[plain.Length + 1], out _, out HandshakeAlert mismatch));
        Assert.AreEqual(HandshakeAlert.DecodeError, mismatch);
        Assert.IsFalse(CertificateCompression.TryDecompress(CertificateCompression.Brotli, compressed, plain.Length, plain.Length - 1, output, out _, out _));
        Assert.IsFalse(CertificateCompression.TryDecompress(CertificateCompression.Brotli, compressed[..^1], plain.Length, 131072, output, out _, out _));
    }
}

internal sealed class CountingEntropy : IEntropySource
{
    private byte _value = 1;

    public void Fill(Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = _value;
            _value = (byte)(_value == 255 ? 1 : _value + 1);
        }
    }
}
