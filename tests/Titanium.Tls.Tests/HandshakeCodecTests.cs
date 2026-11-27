using System;
using System.Buffers;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class HandshakeCodecTests
{
    [TestMethod]
    public void ReaderAndWriterRoundTripBackPatchedLengths()
    {
        byte[] buffer = new byte[32];
        TlsWriter writer = TlsWriter.Over(buffer);
        Assert.IsTrue(writer.TryWriteUInt8(0x16));
        Assert.IsTrue(writer.TryReserveLength(3, out int at));
        Assert.IsTrue(writer.TryWriteUInt16(0x0303));
        Assert.IsTrue(writer.TryWriteVector8([0x01, 0x02]));
        Assert.IsTrue(writer.TryPatchLength(at, 3));
        ArrayBufferWriter<byte> flushed = new();
        Assert.IsTrue(writer.TryFlush(flushed));
        TlsReader reader = new(flushed.WrittenSpan);
        Assert.IsTrue(reader.TryReadUInt8(out byte kind) && kind == 0x16);
        Assert.IsTrue(reader.TryReadUInt24(out int length) && length == 5);
        Assert.IsTrue(reader.TryReadUInt16(out ushort version) && version == 0x0303);
        Assert.IsTrue(reader.TryReadVector8(out ReadOnlySpan<byte> body) && body.SequenceEqual(new byte[] { 0x01, 0x02 }));
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void EveryHandshakeMessageRoundTrips()
    {
        AssertRoundTrip(BuildClientHello(includeExtensions: true), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadClientHello(message, out ClientHelloView view, out _));
            Assert.IsTrue(view.TryWrite(copy, out int written));
            return written;
        });
        AssertRoundTrip(BuildServerHello(helloRetry: false), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadServerHello(message, out ServerHelloView view, out _));
            Assert.IsFalse(view.IsHelloRetryRequest);
            Assert.IsTrue(view.TryWrite(copy, out int written));
            return written;
        });
        AssertRoundTrip(BuildServerHello(helloRetry: true), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadServerHello(message, out ServerHelloView view, out _));
            Assert.IsTrue(view.IsHelloRetryRequest);
            Assert.IsTrue(view.TryWrite(copy, out int written));
            return written;
        });
        byte[] extensions = [0x00, 0x10, 0x00, 0x00];
        AssertRoundTrip(BuildSingleVector(HandshakeType.EncryptedExtensions, 2, extensions), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadEncryptedExtensions(message, out ReadOnlySpan<byte> parsed, out _));
            Assert.IsTrue(HandshakeMessages.TryWriteEncryptedExtensions(copy, parsed, out int written));
            return written;
        });
        AssertRoundTrip(BuildCertificate(), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadCertificate(message, out CertificateView view, out _));
            Assert.IsTrue(view.TryWrite(copy, out int written));
            return written;
        });
        Assert.IsTrue(HandshakeMessages.TryReadCertificateRequest(BuildCertificateRequest(), out _, out _, out HandshakeAlert requestAlert));
        Assert.AreEqual(default, requestAlert);
        Assert.IsTrue(HandshakeMessages.TryReadCertificateVerify(BuildCertificateVerify(), out ushort scheme, out ReadOnlySpan<byte> signature, out _));
        Assert.AreEqual(0x0807, scheme);
        Assert.AreEqual(2, signature.Length);
        Assert.IsTrue(HandshakeMessages.TryReadFinished(BuildFinished(32), out ReadOnlySpan<byte> finished, out _));
        Assert.AreEqual(32, finished.Length);
        Assert.IsTrue(HandshakeMessages.TryReadNewSessionTicket(BuildTicket(), out _, out _, out _, out ReadOnlySpan<byte> ticket, out _, out _));
        Assert.AreEqual(1, ticket.Length);
        Assert.IsTrue(HandshakeMessages.TryReadKeyUpdate(BuildKeyUpdate(1), out byte request, out _));
        Assert.AreEqual(1, request);
        Assert.IsTrue(HandshakeMessages.TryReadEndOfEarlyData(BuildEndOfEarlyData(), out _));
        Assert.IsTrue(HandshakeMessages.TryReadCompressedCertificate(BuildCompressedCertificate(), out ushort algorithm, out int uncompressed, out _, out _));
        Assert.AreEqual(1, algorithm);
        Assert.AreEqual(4, uncompressed);
        Assert.IsTrue(HandshakeMessages.TryReadMessageHash(BuildMessageHash(), out ReadOnlySpan<byte> hash, out _));
        Assert.AreEqual(32, hash.Length);
    }

    [TestMethod]
    public void Rfc8448ServerFlightYieldsTheExpectedTypes()
    {
        byte[] flight = HexVectors.Parse(File.ReadAllText(FindFixture("rfc8448-server-handshake.hex")));
        HandshakeReassembler reassembler = new();
        Assert.IsTrue(reassembler.TryAppend(flight, out _));
        byte[] body = new byte[flight.Length];
        byte[] message = new byte[flight.Length];
        HandshakeType[] expected = [HandshakeType.EncryptedExtensions, HandshakeType.Certificate, HandshakeType.CertificateVerify, HandshakeType.Finished];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.IsTrue(reassembler.TryTake(body, out HandshakeType type, out int length, out bool needMore, out HandshakeAlert alert), expected[i].ToString());
            Assert.IsFalse(needMore);
            Assert.AreEqual(default, alert);
            Assert.AreEqual(expected[i], type);
            message[0] = (byte)type;
            message[1] = (byte)(length >> 16);
            message[2] = (byte)(length >> 8);
            message[3] = (byte)length;
            body.AsSpan(0, length).CopyTo(message.AsSpan(4));
            ReadOnlySpan<byte> framed = message.AsSpan(0, length + 4);
            bool read = Reads(type, framed, out HandshakeAlert readAlert);
            Assert.IsTrue(read, type + " " + readAlert + " " + Convert.ToHexString(framed));
        }
    }

    [TestMethod]
    public void ChromeClientHelloParsesAndReserializes()
    {
        string path = FindFixture(Path.Combine("captures", "chrome", "158.0.8127.89", "tcp", "hello-001.bin"));
        byte[] record = File.ReadAllBytes(path);
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(record, out ReadOnlySpan<byte> payload, out bool incomplete, out HandshakeAlert alert));
        Assert.IsFalse(incomplete);
        Assert.AreEqual(default, alert);
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(payload, out ClientHelloView hello, out alert), alert.ToString());
        Assert.IsTrue(ContainsExtension(hello.Extensions, ExtensionType.KeyShare));
        Assert.IsTrue(ContainsExtension(hello.Extensions, ExtensionType.EncryptedClientHello));
        byte[] copy = new byte[payload.Length];
        Assert.IsTrue(hello.TryWrite(copy, out int written));
        Assert.IsTrue(payload.SequenceEqual(copy.AsSpan(0, written)));
    }

    [TestMethod]
    public void MultiByteLengthsRoundTrip()
    {
        byte[] buffer = new byte[16];
        TlsWriter writer = TlsWriter.Over(buffer);
        Assert.IsTrue(writer.TryWriteUInt24(0x010203));
        Assert.IsTrue(writer.TryWriteUInt32(0x01020304));
        Assert.IsTrue(buffer.AsSpan(0, 7).SequenceEqual(new byte[] { 0x01, 0x02, 0x03, 0x01, 0x02, 0x03, 0x04 }));

        TlsReader reader = new(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.IsTrue(reader.TryReadUInt32(out uint value));
        Assert.AreEqual(0x01020304u, value);

        byte[] framed = new byte[4 + 0x010203];
        framed[0] = (byte)HandshakeType.Certificate;
        framed[1] = 0x01;
        framed[2] = 0x02;
        framed[3] = 0x03;
        framed[4] = 0xAB;
        Assert.IsTrue(HandshakeFraming.TryReadHeader(framed, out HandshakeType type, out int headerLength));
        Assert.AreEqual(HandshakeType.Certificate, type);
        Assert.AreEqual(0x010203, headerLength);
        HandshakeReassembler reassembler = new();
        Assert.IsTrue(reassembler.TryAppend(framed, out HandshakeAlert alert));
        Assert.AreEqual(default, alert);
        byte[] body = new byte[0x010203];
        Assert.IsTrue(reassembler.TryTake(body, out type, out int bodyLength, out bool needMore, out alert));
        Assert.IsFalse(needMore);
        Assert.AreEqual(default, alert);
        Assert.AreEqual(HandshakeType.Certificate, type);
        Assert.AreEqual(0x010203, bodyLength);
        Assert.AreEqual(0xAB, body[0]);
    }

    [TestMethod]
    public void LengthAtTheCapIsDistinctFromOnePastIt()
    {
        int maxRecord = TlsRecordProtector.MaxCiphertext;
        byte[] record = new byte[5];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        record[3] = (byte)(maxRecord >> 8);
        record[4] = (byte)maxRecord;
        Assert.IsFalse(HandshakeFraming.TryUnwrapHandshakeRecord(record, out _, out bool incomplete, out HandshakeAlert alert));
        Assert.IsTrue(incomplete);
        Assert.AreEqual(default, alert);

        int over = HandshakeFraming.DefaultMaxMessageBytes + 256;
        byte[] header =
        [
            (byte)HandshakeType.Certificate,
            (byte)(over >> 16),
            (byte)(over >> 8),
            (byte)over,
        ];
        HandshakeReassembler reassembler = new();
        Assert.IsFalse(reassembler.TryAppend(header, out alert));
        Assert.AreEqual(HandshakeAlert.DecodeError, alert);
    }

    [TestMethod]
    public void ClientHelloCorpusDoesNotThrow()
    {
        string directory = FindFixture(Path.Combine("..", "..", "fuzz", "corpus", "client-hello"));
        foreach (string file in Directory.GetFiles(directory, "*.hex"))
        {
            byte[] record = HexVectors.Parse(File.ReadAllText(file));
            bool unwrapped = HandshakeFraming.TryUnwrapHandshakeRecord(record, out ReadOnlySpan<byte> payload, out bool incomplete, out HandshakeAlert alert);
            string name = Path.GetFileName(file);
            if (name.StartsWith("not-tls", StringComparison.Ordinal) || name.StartsWith("record-length", StringComparison.Ordinal))
            {
                Assert.IsFalse(unwrapped, name);
                Assert.IsFalse(incomplete, name);
                continue;
            }

            if (name.StartsWith("truncated", StringComparison.Ordinal))
            {
                Assert.IsFalse(unwrapped, name);
                Assert.IsTrue(incomplete, name);
                continue;
            }

            Assert.IsTrue(unwrapped, name + " " + alert);
            bool parsed = HandshakeMessages.TryReadClientHello(payload, out ClientHelloView hello, out alert);
            if (name is "extensions-length-overflow.hex" or "session-id-length-overflow.hex" or "handshake-length-huge.hex" or "handshake-length-zero.hex")
            {
                Assert.IsFalse(parsed, name);
            }
            else if (name == "sni-valid.hex")
            {
                Assert.IsTrue(parsed, alert.ToString());
                Assert.IsTrue(TryHost(hello.Extensions, out ReadOnlySpan<byte> host));
                Assert.IsTrue(host.SequenceEqual("a.example"u8));
            }
            else if (name == "no-extensions.hex")
            {
                Assert.IsTrue(parsed, alert.ToString());
                Assert.IsFalse(hello.HasExtensions);
            }
            else
            {
                _ = parsed;
            }
        }
    }

    [TestMethod]
    public void NegativeCodecCasesFailWithTheMappedAlert()
    {
        Assert.IsFalse(HandshakeMessages.TryReadClientHello(BuildDuplicateExtension(), out _, out HandshakeAlert duplicate));
        Assert.AreEqual(HandshakeAlert.IllegalParameter, duplicate);
        Assert.IsFalse(HandshakeMessages.TryReadClientHello(BuildPreSharedKeyNotLast(), out _, out HandshakeAlert order));
        Assert.AreEqual(HandshakeAlert.IllegalParameter, order);
        Assert.IsFalse(HandshakeMessages.TryReadClientHello(BuildEmptyCipherVector(), out _, out HandshakeAlert empty));
        Assert.AreEqual(HandshakeAlert.DecodeError, empty);
        Assert.IsFalse(HandshakeMessages.TryReadClientHello(PatchExtensions(BuildClientHello(includeExtensions: false), [0x00, 0x30, 0x00, 0x00]), out _, out HandshakeAlert oidFilters));
        Assert.AreEqual(HandshakeAlert.IllegalParameter, oidFilters);
        AssertRoundTrip(PatchExtensions(BuildClientHello(includeExtensions: false), []), static (message, copy) =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadClientHello(message, out ClientHelloView view, out _));
            Assert.IsTrue(view.HasExtensions);
            Assert.AreEqual(0, view.Extensions.Length);
            Assert.IsTrue(view.TryWrite(copy, out int written));
            return written;
        });
        Assert.IsFalse(HandshakeMessages.TryReadClientHello(BuildClientHello(includeExtensions: true)[..10], out _, out HandshakeAlert mismatch));
        Assert.AreEqual(HandshakeAlert.DecodeError, mismatch);
        HandshakeReassembler reassembler = new();
        byte[] hello = BuildClientHello(includeExtensions: false);
        Assert.IsTrue(reassembler.TryAppend(hello.AsSpan(0, 6), out _));
        byte[] body = new byte[64];
        Assert.IsTrue(reassembler.TryTake(body, out _, out _, out bool needMore, out _));
        Assert.IsTrue(needMore);
        Assert.IsTrue(reassembler.TryAppend(hello.AsSpan(6), out _));
        Assert.IsTrue(reassembler.TryTake(body, out HandshakeType type, out int length, out needMore, out _));
        Assert.IsFalse(needMore);
        Assert.AreEqual(HandshakeType.ClientHello, type);
        Assert.AreEqual(hello.Length - 4, length);
    }

    [TestMethod]
    public void ClientHelloParseAllocatesNothing()
    {
        byte[] hello = BuildClientHello(includeExtensions: true);
        AllocationGate.AssertNone(() =>
        {
            Assert.IsTrue(HandshakeMessages.TryReadClientHello(hello, out _, out _));
        });
    }

    private static bool Reads(HandshakeType type, ReadOnlySpan<byte> message, out HandshakeAlert alert)
    {
        if (type == HandshakeType.EncryptedExtensions)
        {
            return HandshakeMessages.TryReadEncryptedExtensions(message, out _, out alert);
        }

        if (type == HandshakeType.Certificate)
        {
            return HandshakeMessages.TryReadCertificate(message, out _, out alert);
        }

        if (type == HandshakeType.CertificateVerify)
        {
            return HandshakeMessages.TryReadCertificateVerify(message, out _, out _, out alert);
        }

        if (type == HandshakeType.Finished)
        {
            return HandshakeMessages.TryReadFinished(message, out _, out alert);
        }

        alert = default;
        return false;
    }

    private static bool ContainsExtension(ReadOnlySpan<byte> extensions, ushort type)
    {
        TlsReader reader = new(extensions);
        while (reader.TryReadUInt16(out ushort found) && reader.TryReadVector16(out _))
        {
            if (found == type)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryHost(ReadOnlySpan<byte> extensions, out ReadOnlySpan<byte> host)
    {
        host = default;
        TlsReader reader = new(extensions);
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            if (type == ExtensionType.ServerName && HandshakeMessages.TryReadHostName(body, out host))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertRoundTrip(byte[] message, Func<ReadOnlySpan<byte>, Span<byte>, int> rewrite)
    {
        byte[] copy = new byte[message.Length];
        int written = rewrite(message, copy);
        Assert.IsTrue(message.AsSpan().SequenceEqual(copy.AsSpan(0, written)));
    }

    private static byte[] BuildClientHello(bool includeExtensions)
    {
        byte[] buffer = new byte[128];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.ClientHello);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt16(0x0303);
        writer.TryWrite(new byte[32]);
        writer.TryWriteVector8([]);
        writer.TryWriteVector16([0x13, 0x01]);
        writer.TryWriteVector8([0x00]);
        if (includeExtensions)
        {
            writer.TryWriteVector16([0x00, 0x00, 0x00, 0x05, 0x00, 0x03, 0x00, 0x00, 0x00]);
        }

        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildEmptyCipherVector()
    {
        byte[] buffer = new byte[64];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.ClientHello);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt16(0x0303);
        writer.TryWrite(new byte[32]);
        writer.TryWriteVector8([]);
        writer.TryWriteVector16([]);
        writer.TryWriteVector8([0x00]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildServerHello(bool helloRetry)
    {
        byte[] random = new byte[32];
        if (helloRetry)
        {
            random = Convert.FromHexString("CF21AD74E59A6111BE1D8C021E65B891C2A211167ABB8C5E079E09E2C8A8339C");
        }

        byte[] buffer = new byte[128];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.ServerHello);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt16(0x0303);
        writer.TryWrite(random);
        writer.TryWriteVector8([]);
        writer.TryWriteUInt16(0x1301);
        writer.TryWriteUInt8(0);
        writer.TryWriteVector16([0x00, 0x2b, 0x00, 0x02, 0x03, 0x04]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildCertificate()
    {
        byte[] buffer = new byte[32];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.Certificate);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteVector8([]);
        writer.TryWriteVector24([0x00, 0x00, 0x01, 0x30, 0x00, 0x00]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildCertificateRequest()
    {
        return BuildSingleVector(HandshakeType.CertificateRequest, 1, [0x00, 0x0d, 0x00, 0x00]);
    }

    private static byte[] BuildCertificateVerify()
    {
        byte[] buffer = new byte[16];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.CertificateVerify);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt16(0x0807);
        writer.TryWriteVector16([0xaa, 0xbb]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildFinished(int size)
    {
        byte[] buffer = new byte[size + 4];
        buffer[0] = (byte)HandshakeType.Finished;
        buffer[3] = (byte)size;
        return buffer;
    }

    private static byte[] BuildTicket()
    {
        byte[] buffer = new byte[32];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.NewSessionTicket);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt32(1);
        writer.TryWriteUInt32(2);
        writer.TryWriteVector8([]);
        writer.TryWriteVector16([0xaa]);
        writer.TryWriteVector16([]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildKeyUpdate(byte request)
    {
        return [(byte)HandshakeType.KeyUpdate, 0, 0, 1, request];
    }

    private static byte[] BuildEndOfEarlyData()
    {
        return [(byte)HandshakeType.EndOfEarlyData, 0, 0, 0];
    }

    private static byte[] BuildCompressedCertificate()
    {
        byte[] buffer = new byte[16];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)HandshakeType.CompressedCertificate);
        writer.TryReserveLength(3, out int at);
        writer.TryWriteUInt16(1);
        writer.TryWriteUInt24(4);
        writer.TryWriteVector24([0x01]);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildMessageHash()
    {
        byte[] buffer = new byte[36];
        buffer[0] = (byte)HandshakeType.MessageHash;
        buffer[3] = 32;
        return buffer;
    }

    private static byte[] BuildSingleVector(HandshakeType type, int width, byte[] body)
    {
        byte[] buffer = new byte[body.Length + 8];
        TlsWriter writer = TlsWriter.Over(buffer);
        writer.TryWriteUInt8((byte)type);
        writer.TryReserveLength(3, out int at);
        if (width == 1)
        {
            writer.TryWriteVector8([]);
        }

        writer.TryWriteVector16(body);
        writer.TryPatchLength(at, 3);
        return buffer[..writer.Written];
    }

    private static byte[] BuildDuplicateExtension()
    {
        return PatchExtensions(BuildClientHello(includeExtensions: false), [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
    }

    private static byte[] BuildPreSharedKeyNotLast()
    {
        return PatchExtensions(BuildClientHello(includeExtensions: false), [0x00, 0x29, 0x00, 0x00, 0x00, 0x15, 0x00, 0x00]);
    }

    private static byte[] PatchExtensions(byte[] bareHello, byte[] extensions)
    {
        byte[] buffer = new byte[bareHello.Length + extensions.Length + 2];
        bareHello.AsSpan(0, bareHello.Length).CopyTo(buffer);
        int body = ((buffer[1] << 16) | (buffer[2] << 8) | buffer[3]) + 2 + extensions.Length;
        buffer[1] = (byte)(body >> 16);
        buffer[2] = (byte)(body >> 8);
        buffer[3] = (byte)body;
        buffer[bareHello.Length] = (byte)(extensions.Length >> 8);
        buffer[bareHello.Length + 1] = (byte)extensions.Length;
        extensions.CopyTo(buffer.AsSpan(bareHello.Length + 2));
        return buffer[..(bareHello.Length + 2 + extensions.Length)];
    }

    private static string FindFixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Titanium.Tls.Tests", "Fixtures", name);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(name);
    }
}
