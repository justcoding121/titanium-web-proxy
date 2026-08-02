using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for HTTP/3 SETTINGS parse/serialize (RFC 9114 §7.2.4 + QPACK).
/// </summary>
[TestClass]
public class Http3SettingsTests
{
    [TestMethod]
    public void SerializeThenParse_RoundTripsKnownParameters()
    {
        var original = new Http3Settings();
        original.Add(Http3SettingsId.MaxFieldSectionSize, 65536);
        original.SetQpackMaxTableCapacity(4096);
        original.SetQpackBlockedStreams(0);

        var parsed = Http3Settings.Parse(original.Serialize());

        Assert.AreEqual(65536UL, parsed.MaxFieldSectionSize);
        Assert.AreEqual(4096u, parsed.QpackMaxTableCapacity);
        Assert.AreEqual(0u, parsed.QpackBlockedStreams);
        Assert.AreEqual(3, parsed.Parameters.Count);
    }

    [TestMethod]
    public void Parse_EmptyPayload_ReturnsDefaults()
    {
        var settings = Http3Settings.Parse(ReadOnlySpan<byte>.Empty);

        Assert.AreEqual(0UL, settings.MaxFieldSectionSize);
        Assert.AreEqual(0u, settings.QpackMaxTableCapacity);
        Assert.AreEqual(0u, settings.QpackBlockedStreams);
        Assert.AreEqual(0, settings.Parameters.Count);
    }

    [TestMethod]
    public void Parse_UnknownSettingId_IsPreservedButIgnoredForTypedProperties()
    {
        var settings = new Http3Settings();
        settings.Add(0x2A, 99); // unknown id — must be ignored per RFC 9114

        var parsed = Http3Settings.Parse(settings.Serialize());

        Assert.AreEqual(1, parsed.Parameters.Count);
        Assert.AreEqual(0x2AUL, parsed.Parameters[0].Id);
        Assert.AreEqual(99UL, parsed.Parameters[0].Value);
        Assert.AreEqual(0UL, parsed.MaxFieldSectionSize);
    }

    [TestMethod]
    public void Parse_TruncatedPair_StopsWithoutThrowing()
    {
        // Complete (id=6, value=1) then a truncated second id.
        var buf = new byte[8];
        var offset = 0;
        offset += Http3VarInt.Write(buf.AsSpan(offset), Http3SettingsId.MaxFieldSectionSize);
        offset += Http3VarInt.Write(buf.AsSpan(offset), 1);
        buf[offset] = 0x40; // starts a 2-byte varint with no second byte

        var parsed = Http3Settings.Parse(buf.AsSpan(0, offset + 1));

        Assert.AreEqual(1UL, parsed.MaxFieldSectionSize);
        Assert.AreEqual(1, parsed.Parameters.Count);
    }

    [TestMethod]
    public void Add_QpackMaxTableCapacity_ClampsToUIntMax()
    {
        var settings = new Http3Settings();
        settings.Add(Http3SettingsId.QpackMaxTableCapacity, (ulong)uint.MaxValue + 10);

        Assert.AreEqual(uint.MaxValue, settings.QpackMaxTableCapacity);
    }
}
