using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicTransportParameterTests
{
    [TestMethod]
    public void ClientParametersRoundTrip()
    {
        var written = new QuicTransportParameters
        {
            InitialSourceConnectionId = [0xaa],
            MaxIdleTimeout = 30_000,
            MaxUdpPayloadSize = 1452,
            InitialMaxData = 1000,
            InitialMaxStreamDataBidiLocal = 100,
            InitialMaxStreamDataBidiRemote = 200,
            InitialMaxStreamDataUni = 50,
            InitialMaxStreamsBidi = 4,
            InitialMaxStreamsUni = 2,
            AckDelayExponent = 10,
            MaxAckDelay = 20,
            DisableActiveMigration = true,
            ActiveConnectionIdLimit = 4,
            MaxDatagramFrameSize = 65535,
            GreaseQuicBit = true,
        };
        QuicTransportParameters read = RoundTrip(written, fromServer: false, retryReceived: false);
        Assert.AreEqual(0xaa, read.InitialSourceConnectionId![0]);
        Assert.IsNull(read.OriginalDestinationConnectionId);
        Assert.IsNull(read.RetrySourceConnectionId);
        Assert.IsNull(read.StatelessResetToken);
        Assert.IsFalse(read.HasPreferredAddress);
        Assert.AreEqual(30_000UL, read.MaxIdleTimeout);
        Assert.AreEqual(1452UL, read.MaxUdpPayloadSize);
        Assert.AreEqual(1000UL, read.InitialMaxData);
        Assert.AreEqual(100UL, read.InitialMaxStreamDataBidiLocal);
        Assert.AreEqual(200UL, read.InitialMaxStreamDataBidiRemote);
        Assert.AreEqual(50UL, read.InitialMaxStreamDataUni);
        Assert.AreEqual(4UL, read.InitialMaxStreamsBidi);
        Assert.AreEqual(2UL, read.InitialMaxStreamsUni);
        Assert.AreEqual(10UL, read.AckDelayExponent);
        Assert.AreEqual(20UL, read.MaxAckDelay);
        Assert.IsTrue(read.DisableActiveMigration);
        Assert.AreEqual(4UL, read.ActiveConnectionIdLimit);
        Assert.AreEqual(65535UL, read.MaxDatagramFrameSize);
        Assert.IsTrue(read.GreaseQuicBit);
    }

    [TestMethod]
    public void ServerParametersRoundTripIncludingPreferredAddress()
    {
        byte[] reset = new byte[16];
        reset[0] = 0x7e;
        var written = Server(retry: false);
        written.StatelessResetToken = reset;
        written.HasPreferredAddress = true;
        written.PreferredAddress.IPv4 = [192, 0, 2, 1];
        written.PreferredAddress.IPv4Port = 443;
        written.PreferredAddress.IPv6[0] = 0x20;
        written.PreferredAddress.IPv6[1] = 0x01;
        written.PreferredAddress.IPv6[15] = 0x01;
        written.PreferredAddress.IPv6Port = 443;
        written.PreferredAddress.ConnectionId = [0xdd];
        written.PreferredAddress.StatelessResetToken[1] = 0x11;
        QuicTransportParameters read = RoundTrip(written, fromServer: true, retryReceived: false);
        CollectionAssert.AreEqual(new byte[] { 0xbb }, read.OriginalDestinationConnectionId);
        CollectionAssert.AreEqual(new byte[] { 0xcc }, read.InitialSourceConnectionId);
        CollectionAssert.AreEqual(reset, read.StatelessResetToken);
        Assert.IsNull(read.RetrySourceConnectionId);
        Assert.IsTrue(read.HasPreferredAddress);
        CollectionAssert.AreEqual(new byte[] { 192, 0, 2, 1 }, read.PreferredAddress.IPv4);
        Assert.AreEqual((ushort)443, read.PreferredAddress.IPv4Port);
        Assert.AreEqual(0x20, read.PreferredAddress.IPv6[0]);
        Assert.AreEqual(0x01, read.PreferredAddress.IPv6[15]);
        Assert.AreEqual((ushort)443, read.PreferredAddress.IPv6Port);
        CollectionAssert.AreEqual(new byte[] { 0xdd }, read.PreferredAddress.ConnectionId);
        Assert.AreEqual(0x11, read.PreferredAddress.StatelessResetToken[1]);
    }

    [TestMethod]
    public void MissingRequiredParametersAreRejected()
    {
        Assert.IsFalse(Read(new QuicTransportParameters(), fromServer: false, retryReceived: false));
        var server = Server(retry: false);
        server.InitialSourceConnectionId = null;
        Assert.IsFalse(Read(server, fromServer: true, retryReceived: false));
        server = Server(retry: false);
        server.OriginalDestinationConnectionId = null;
        Assert.IsFalse(Read(server, fromServer: true, retryReceived: false));
        Assert.IsFalse(Read(Server(retry: false), fromServer: true, retryReceived: true));
        var client = new QuicTransportParameters
        {
            InitialSourceConnectionId = [0xaa],
            OriginalDestinationConnectionId = [0xbb],
        };
        Assert.IsFalse(Read(client, fromServer: false, retryReceived: false));
    }

    [TestMethod]
    public void ADuplicateParameterIsRejected()
    {
        byte[] once = Parameter(0x0f, [0xaa]);
        byte[] twice = new byte[once.Length * 2];
        once.CopyTo(twice, 0);
        once.CopyTo(twice, once.Length);
        Assert.IsFalse(QuicTransportParametersCodec.TryRead(twice, fromServer: false, retryReceived: false, out _, out ulong error));
        Assert.AreEqual(QuicTransportParametersCodec.TransportParameterError, error);
    }

    [TestMethod]
    public void AGreaseParameterIsIgnored()
    {
        byte[] grease = Parameter(27, [0x01, 0x02]);
        byte[] source = Parameter(0x0f, [0xaa]);
        byte[] combined = new byte[grease.Length + source.Length];
        grease.CopyTo(combined, 0);
        source.CopyTo(combined, grease.Length);
        Assert.IsTrue(QuicTransportParametersCodec.TryRead(combined, fromServer: false, retryReceived: false, out QuicTransportParameters read, out _));
        CollectionAssert.AreEqual(new byte[] { 0xaa }, read.InitialSourceConnectionId);
        Assert.IsFalse(read.GreaseQuicBit);
    }

    [TestMethod]
    public void NumericAndLengthEdgesStayInsideTheSpec()
    {
        Assert.IsFalse(ReadInteger(0x03, 1199, out _));
        Assert.IsTrue(ReadInteger(0x03, 1200, out QuicTransportParameters read));
        Assert.AreEqual(1200UL, read.MaxUdpPayloadSize);
        Assert.IsTrue(ReadInteger(0x0a, 20, out read));
        Assert.AreEqual(20UL, read.AckDelayExponent);
        Assert.IsFalse(ReadInteger(0x0a, 21, out _));
        Assert.IsTrue(ReadInteger(0x0b, (1UL << 14) - 1, out read));
        Assert.AreEqual((1UL << 14) - 1, read.MaxAckDelay);
        Assert.IsFalse(ReadInteger(0x0b, 1UL << 14, out _));
        Assert.IsTrue(ReadInteger(0x0e, 2, out read));
        Assert.AreEqual(2UL, read.ActiveConnectionIdLimit);
        Assert.IsFalse(ReadInteger(0x0e, 1, out _));
        Assert.IsTrue(ReadInteger(0x08, QuicTransportParametersCodec.MaxStreamsLimit, out read));
        Assert.AreEqual(QuicTransportParametersCodec.MaxStreamsLimit, read.InitialMaxStreamsBidi);
        Assert.IsFalse(ReadInteger(0x08, QuicTransportParametersCodec.MaxStreamsLimit + 1, out _));
        Assert.IsFalse(ReadWithSource(Parameter(0x02, new byte[15])));
        Assert.IsFalse(QuicTransportParametersCodec.TryRead(Parameter(0x0f, new byte[21]), fromServer: false, retryReceived: false, out _, out _));
        Assert.IsTrue(QuicTransportParametersCodec.TryRead(Parameter(0x0f, new byte[20]), fromServer: false, retryReceived: false, out read, out _));
        Assert.AreEqual(20, read.InitialSourceConnectionId!.Length);
        Assert.IsFalse(ReadWithSource(Parameter(0x0c, [0x00])));
        Assert.IsFalse(ReadWithSource([0x01, 0x02, 0x00]));

        var parameters = new QuicTransportParameters { InitialSourceConnectionId = [0xaa] };
        byte[] wide = new byte[64];
        int written = QuicTransportParametersCodec.Write(parameters, wide);
        Assert.IsTrue(written > 1);
        Assert.AreEqual(written, QuicTransportParametersCodec.Write(parameters, wide.AsSpan(0, written)));
        Assert.AreEqual(0, QuicTransportParametersCodec.Write(parameters, wide.AsSpan(0, written - 1)));
    }

    [TestMethod]
    public void RetrySourceIsRequiredOnlyAfterARetry()
    {
        QuicTransportParameters withRetry = Server(retry: true);
        Assert.IsTrue(QuicTransportParametersCodec.TryRead(Encode(withRetry), fromServer: true, retryReceived: true, out QuicTransportParameters read, out _));
        CollectionAssert.AreEqual(new byte[] { 0xee }, read.RetrySourceConnectionId);
        Assert.IsFalse(QuicTransportParametersCodec.TryRead(Encode(withRetry), fromServer: true, retryReceived: false, out _, out ulong error));
        Assert.AreEqual(QuicTransportParametersCodec.TransportParameterError, error);
    }

    private static QuicTransportParameters Server(bool retry)
    {
        var parameters = new QuicTransportParameters
        {
            OriginalDestinationConnectionId = [0xbb],
            InitialSourceConnectionId = [0xcc],
        };
        if (retry)
        {
            parameters.RetrySourceConnectionId = [0xee];
        }

        return parameters;
    }

    private static QuicTransportParameters RoundTrip(QuicTransportParameters written, bool fromServer, bool retryReceived)
    {
        Assert.IsTrue(QuicTransportParametersCodec.TryRead(Encode(written), fromServer, retryReceived, out QuicTransportParameters read, out ulong error));
        Assert.AreEqual(0UL, error);
        return read;
    }

    private static bool Read(QuicTransportParameters written, bool fromServer, bool retryReceived)
    {
        bool ok = QuicTransportParametersCodec.TryRead(Encode(written), fromServer, retryReceived, out _, out ulong error);
        Assert.AreEqual(QuicTransportParametersCodec.TransportParameterError, error);
        return ok;
    }

    private static bool ReadInteger(ulong id, ulong value, out QuicTransportParameters read)
    {
        byte[] number = new byte[8];
        int count = QuicVarInt.Encode(value, number);
        return ReadWithSource(Parameter(id, number[..count]), out read);
    }

    private static bool ReadWithSource(byte[] parameter)
    {
        return ReadWithSource(parameter, out _);
    }

    private static bool ReadWithSource(byte[] parameter, out QuicTransportParameters read)
    {
        byte[] source = Parameter(0x0f, [0xaa]);
        byte[] combined = new byte[parameter.Length + source.Length];
        parameter.CopyTo(combined, 0);
        source.CopyTo(combined, parameter.Length);
        return QuicTransportParametersCodec.TryRead(combined, fromServer: false, retryReceived: false, out read, out _);
    }

    private static byte[] Encode(QuicTransportParameters parameters)
    {
        var buffer = new byte[256];
        int written = QuicTransportParametersCodec.Write(parameters, buffer);
        Assert.IsTrue(written > 0);
        return buffer[..written];
    }

    private static byte[] Parameter(ulong id, byte[] value)
    {
        var buffer = new byte[16 + value.Length];
        int at = QuicVarInt.Encode(id, buffer);
        at += QuicVarInt.Encode((ulong)value.Length, buffer.AsSpan(at));
        value.CopyTo(buffer.AsSpan(at));
        return buffer[..(at + value.Length)];
    }
}
