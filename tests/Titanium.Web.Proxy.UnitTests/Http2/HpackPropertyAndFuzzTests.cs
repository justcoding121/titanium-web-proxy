using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Property and mutation tests for HPACK encode/decode.
///     Bug class: dynamic-table corruption, undocumented throws, or hangs on truncated blocks.
/// </summary>
[TestClass]
public class HpackPropertyAndFuzzTests
{
    private static readonly TimeSpan CaseDeadline = TimeSpan.FromSeconds(5);

    [TestMethod]
    [Timeout(60_000)]
    public void EncodeDecode_RoundTrips_SeededRandomHeaderLists()
    {
        // Bug class: encode/decode drift or dynamic-table size accounting error.
        var rng = new Random(20261108);
        for (var i = 0; i < 1000; i++)
        {
            var tableSize = rng.Next(0, 2) == 0 ? 0 : rng.Next(64, 8192);
            var headers = BuildRandomHeaders(rng, count: rng.Next(1, 12));
            var encoded = EncodeAll(new Encoder(tableSize), headers);
            var decoded = DecodeAll(new Decoder(maxHeaderSize: 256 * 1024, maxHeaderTableSize: tableSize), encoded);
            Assert.AreEqual(headers.Count, decoded.Count, $"case {i} header count");
            for (var h = 0; h < headers.Count; h++)
            {
                Assert.AreEqual(headers[h].Name, decoded[h].Name, $"case {i} name {h}");
                Assert.AreEqual(headers[h].Value, decoded[h].Value, $"case {i} value {h}");
            }
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public void Decoder_TruncatedAndMutatedBlocks_NeverHangOrThrowUndocumented()
    {
        // Bug class: truncated HPACK blocks hang the decoder or throw unexpected types.
        var rng = new Random(911);
        var seeds = new List<byte[]>();
        for (var i = 0; i < 40; i++)
        {
            var headers = BuildRandomHeaders(rng, count: rng.Next(1, 8));
            seeds.Add(EncodeAll(new Encoder(4096), headers));
        }

        // Static-table indexed scheme/https and a literal with Huffman-ish bytes.
        seeds.Add([0x87]);
        seeds.Add(EncodeAll(new Encoder(4096), [("x-bin", "\0\r\n\xff")] ));

        var violations = new List<string>();
        foreach (var seed in seeds)
        {
            for (var len = 0; len <= seed.Length; len++)
            {
                CheckMutant(seed.AsSpan(0, len).ToArray(), violations);
            }

            for (var m = 0; m < 8; m++)
            {
                var mutant = (byte[])seed.Clone();
                if (mutant.Length == 0)
                {
                    continue;
                }

                var at = rng.Next(mutant.Length);
                mutant[at] ^= (byte)(1 << rng.Next(8));
                CheckMutant(mutant, violations);
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join("; ", violations));
    }

    [TestMethod]
    [Timeout(15_000)]
    public void Encoder_SetMaxHeaderTableSize_ThenRoundTrip()
    {
        // Bug class: table-size update is not encoded or leaves decoder out of sync.
        var encoder = new Encoder(4096);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        encoder.SetMaxHeaderTableSize(writer, 256);
        encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes("x-a"), Encoding.ASCII.GetBytes("1"));
        writer.Flush();

        var decoder = new Decoder(8192, 4096);
        var listener = new RecordingHeaderListener();
        decoder.SetMaxHeaderTableSize(256);
        decoder.Decode(ms.ToArray(), listener);
        decoder.EndHeaderBlock();
        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("x-a", listener.Headers[0].Name);
        Assert.AreEqual("1", listener.Headers[0].Value);
        Assert.AreEqual(256, decoder.GetMaxHeaderTableSize());
    }

    [TestMethod]
    [Timeout(15_000)]
    public void Decoder_OversizedHeaderList_FailsEndHeaderBlock()
    {
        // Bug class: MaxHeaderSize is not enforced and large lists decode silently.
        var encoder = new Encoder(4096);
        var huge = new string('a', 200);
        var encoded = EncodeAll(encoder, [("x-big", huge), ("x-big2", huge), ("x-big3", huge)]);
        var decoder = new Decoder(maxHeaderSize: 100, maxHeaderTableSize: 4096);
        var listener = new RecordingHeaderListener();
        try
        {
            decoder.Decode(encoded, listener);
            var overflow = decoder.EndHeaderBlock();
            Assert.IsTrue(overflow || listener.Headers.Count == 0,
                "decoder must refuse or flag an oversized header list");
        }
        catch (Exception ex) when (IsDocumentedDecodeFailure(ex))
        {
            // Documented rejection is acceptable.
        }
    }

    private static void CheckMutant(byte[] mutant, List<string> violations)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var decoder = new Decoder(8192, 4096);
                var listener = new RecordingHeaderListener();
                decoder.Decode(mutant, listener);
                decoder.EndHeaderBlock();
            });

            if (!task.Wait(CaseDeadline))
            {
                violations.Add($"hang on {Convert.ToHexString(mutant)}");
                return;
            }

            if (task.Exception is null)
            {
                return;
            }

            foreach (var ex in task.Exception.Flatten().InnerExceptions)
            {
                if (!IsDocumentedDecodeFailure(ex))
                {
                    violations.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (AggregateException ex)
        {
            foreach (var inner in ex.Flatten().InnerExceptions)
            {
                if (!IsDocumentedDecodeFailure(inner))
                {
                    violations.Add($"{inner.GetType().Name}: {inner.Message}");
                }
            }
        }
        catch (Exception ex) when (IsDocumentedDecodeFailure(ex))
        {
        }
        catch (Exception ex)
        {
            violations.Add($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsDocumentedDecodeFailure(Exception ex) =>
        ex is IOException or ArgumentException or InvalidOperationException or FormatException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException;

    private static List<(string Name, string Value)> BuildRandomHeaders(Random rng, int count)
    {
        var list = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var name = "x-" + RandomToken(rng, rng.Next(1, 12));
            var value = RandomToken(rng, rng.Next(0, 40));
            list.Add((name, value));
        }

        return list;
    }

    private static string RandomToken(Random rng, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-._~";
        if (length == 0)
        {
            return string.Empty;
        }

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    private static byte[] EncodeAll(Encoder encoder, IReadOnlyList<(string Name, string Value)> headers)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        foreach (var (name, value) in headers)
        {
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
        }

        writer.Flush();
        return ms.ToArray();
    }

    private static List<(string Name, string Value)> DecodeAll(Decoder decoder, byte[] encoded)
    {
        var listener = new RecordingHeaderListener();
        decoder.Decode(encoded, listener);
        decoder.EndHeaderBlock();
        return listener.Headers;
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        internal List<(string Name, string Value)> Headers { get; } = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive) =>
            Headers.Add((name.ToString(), value.ToString()));
    }
}
