using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2SchemePatchTests
{
    [TestMethod]
    public void TryPatchStaticIndexedScheme_RewritesHttpsToHttp()
    {
        var block = EncodeMinimalRequest(https: true);
        Assert.IsTrue(
            Http2Helper.TryPatchStaticIndexedScheme(block, ProxyServer.UriSchemeHttps8,
                ProxyServer.UriSchemeHttp8, out var patched),
            "Expected indexed :scheme https (0x87) to be patchable. block=" + BitConverter.ToString(block));

        Assert.AreNotEqual(block[FindSchemeByteIndex(block)], patched[FindSchemeByteIndex(patched)]);
        Assert.AreEqual(0x86, patched[FindSchemeByteIndex(patched)]);
        AssertDecodedScheme(patched, "http");
    }

    [TestMethod]
    public void TryPatchStaticIndexedScheme_RewritesHttpToHttps()
    {
        var block = EncodeMinimalRequest(https: false);
        Assert.IsTrue(
            Http2Helper.TryPatchStaticIndexedScheme(block, ProxyServer.UriSchemeHttp8,
                ProxyServer.UriSchemeHttps8, out var patched));
        Assert.AreEqual(0x87, patched[FindSchemeByteIndex(patched)]);
        AssertDecodedScheme(patched, "https");
    }

    [TestMethod]
    public void TryPatchStaticIndexedScheme_FailsWhenSchemeAlreadyMatches()
    {
        var block = EncodeMinimalRequest(https: true);
        Assert.IsFalse(
            Http2Helper.TryPatchStaticIndexedScheme(block, ProxyServer.UriSchemeHttps8,
                ProxyServer.UriSchemeHttps8, out _));
    }

    [TestMethod]
    public void TryApplyStaticIndexedSchemeOverride_PatchesWithoutDecoder()
    {
        var block = EncodeMinimalRequest(https: false);
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.Patched,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(block, ProxyServer.UriSchemeHttps8,
                out var patched));
        Assert.AreEqual(0x87, patched[FindSchemeByteIndex(patched)]);
        AssertDecodedScheme(patched, "https");
    }

    [TestMethod]
    public void TryApplyStaticIndexedSchemeOverride_AlreadyMatching()
    {
        var block = EncodeMinimalRequest(https: true);
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.AlreadyMatching,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(block, ProxyServer.UriSchemeHttps8,
                out _));
    }

    private static byte[] EncodeMinimalRequest(bool https)
    {
        // HEADER_TABLE_SIZE=0 so :scheme is a static Indexed Header Field (6/7).
        var encoder = new Encoder(0);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, "GET".GetByteString());
        encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, "example.test".GetByteString());
        encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme,
            (https ? "https" : "http").GetByteString());
        encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, "/".GetByteString(), false,
            HpackUtil.IndexType.None, false);
        writer.Flush();
        return ms.ToArray();
    }

    private static int FindSchemeByteIndex(byte[] block)
    {
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] is 0x86 or 0x87)
                return i;
        }

        Assert.Fail("No indexed :scheme byte in block.");
        return -1;
    }

    private static void AssertDecodedScheme(byte[] block, string expected)
    {
        var decoder = new Decoder(8192, 0);
        string? scheme = null;
        decoder.Decode(block, new SchemeListener(s => scheme = s));
        decoder.EndHeaderBlock();
        Assert.AreEqual(expected, scheme);
    }

    private sealed class SchemeListener(System.Action<string> onScheme) : IHeaderListener
    {
        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            if (name.Equals(StaticTable.KnownHeaderScheme))
                onScheme(value.GetString());
        }
    }
}
