using System;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Titanium.Web.Proxy.Benchmarks.Support;

namespace Titanium.Web.Proxy.Benchmarks;

/// <summary>
///     Measures the cost of reading and parsing a run of chunk-size lines, using the real line
///     reader (<see cref="Titanium.Web.Proxy.Helpers.HttpStream.ReadLineInternalAsync" />) plus the
///     same extension-stripping and <see cref="int.TryParse(string, NumberStyles, System.IFormatProvider, out int)" />
///     hex parse that <c>LimitedStream.GetNextChunkAsync</c> uses today. This is a pre-hardening
///     baseline: the plan's HTTP/1 framing security work (grammar-conformant chunk parsing, rejecting
///     negative/oversized chunks) replaces the parse call here, and this benchmark should be updated
///     alongside that change so the comparison stays apples-to-apples across phase boundaries.
/// </summary>
[MemoryDiagnoser]
public class ChunkSizeLineParseBenchmarks
{
    [Params(10, 100, 1000)]
    public int ChunkCount { get; set; }

    private byte[] chunkSizeLinesBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ChunkCount; i++)
            sb.Append((i % 0x3fff + 1).ToString("x")).Append("\r\n");
        chunkSizeLinesBytes = Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Benchmark]
    public async System.Threading.Tasks.Task ParseChunkSizeLines()
    {
        var bufferPool = new SimpleBufferPool();
        var lineStream = new InMemoryLineStream(chunkSizeLinesBytes, bufferPool);

        var total = 0;
        for (var i = 0; i < ChunkCount; i++)
        {
            var chunkHead = await lineStream.ReadLineAsync();
            if (chunkHead is null) break;

            var idx = chunkHead.IndexOf(';');
            if (idx >= 0) chunkHead = chunkHead[..idx];

            if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
                throw new InvalidOperationException($"Invalid chunk length: '{chunkHead}'");

            total += chunkSize;
        }

        if (total < 0) throw new InvalidOperationException("unreachable");
    }
}
