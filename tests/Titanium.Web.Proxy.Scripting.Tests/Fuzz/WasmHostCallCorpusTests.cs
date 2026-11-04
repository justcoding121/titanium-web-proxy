using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.UnitTests.Fuzz;

namespace Titanium.Web.Proxy.Scripting.Tests.Fuzz;

/// <summary>
/// Feeds the proxy-wasm host-call corpus under <c>tests/fuzz/corpus</c> (<c>proxy-wasm-header-map</c>,
/// <c>proxy-wasm-property-path</c>) into <see cref="WasmHostCallFuzzTarget" />, then replays a fixed-seed mutation pass over
/// every seed. The contract: whatever bytes a guest hands the host, the host answers with an error code. It never throws,
/// hangs, or accepts a map or path it cannot read back. These tests need no native library.
/// </summary>
[TestClass]
public class WasmHostCallCorpusTests
{
    private const int MutationsPerSeed = 300;
    private const int MaxReportedFailures = 12;

    private static readonly byte[] InterestingBytes =
        [0, 0, 0, 0, 0xff, 0xff, 0xff, 0x7f, 0x80, 1, 2, (byte)'.', (byte)':', 0x0a, 0xc3, 0xa9];

    [TestMethod]
    public void HeaderMapCorpus_HasSeedsForEveryMalformedShape()
    {
        var seeds = FuzzCorpus.Load("proxy-wasm-header-map", ".hex");
        var names = seeds.Select(s => s.Name).ToList();

        Assert.IsTrue(seeds.Count >= 12, $"expected a header-map corpus, found {seeds.Count} seeds");
        foreach (var prefix in new[] { "valid-", "short-", "count-", "length-", "truncated-", "utf8-" })
        {
            Assert.IsTrue(names.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)), $"no seed for the '{prefix}' class");
        }

        foreach (var seed in seeds)
        {
            Assert.IsNotNull(seed.Expect, $"{seed.Name}: every seed needs an '# expect:' directive");
        }
    }

    [TestMethod]
    public void HeaderMapCorpus_EverySeedMeetsItsExpectation()
    {
        var failures = new List<string>();
        foreach (var seed in FuzzCorpus.Load("proxy-wasm-header-map", ".hex"))
        {
            var outcome = WasmHostCallFuzzTarget.CheckHeaderMap(seed.Bytes);
            failures.AddRange(outcome.Violations.Select(v => $"{seed.Name}: {v}"));

            var expect = seed.Expect ?? string.Empty;
            if (expect == "malformed" && outcome.Parsed)
            {
                failures.Add($"{seed.Name}: expected a ParseFailure, decoded {outcome.Count} pairs");
            }
            else if (expect.StartsWith("parsed:", StringComparison.Ordinal))
            {
                var count = int.Parse(expect["parsed:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                if (!outcome.Parsed || outcome.Count != count)
                {
                    failures.Add($"{seed.Name}: expected {count} pairs, got {(outcome.Parsed ? outcome.Count.ToString() : "ParseFailure")}");
                }
            }
            else if (expect != "malformed" && expect != "any" && !expect.StartsWith("parsed:", StringComparison.Ordinal))
            {
                failures.Add($"{seed.Name}: unknown expect '{expect}'");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void HeaderMapCorpus_MutationsNeverThrowOrBreakTheRoundTrip()
    {
        var failures = new List<string>();
        var random = new Random(20261101);
        var executed = 0;

        foreach (var seed in FuzzCorpus.Load("proxy-wasm-header-map", ".hex"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var outcome = WasmHostCallFuzzTarget.CheckHeaderMap(input);
                if (outcome.Violations.Count > 0)
                {
                    failures.Add($"{seed.Name}: {string.Join("; ", outcome.Violations)} for input '{Convert.ToHexString(input)}'");
                }
            }
        }

        Assert.IsTrue(executed > 2000, $"mutation pass ran only {executed} inputs");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void PropertyPathCorpus_EverySeedMeetsItsExpectation()
    {
        var seeds = FuzzCorpus.Load("proxy-wasm-property-path", ".case");
        Assert.IsTrue(seeds.Count >= 10, $"expected a property-path corpus, found {seeds.Count} seeds");

        var failures = new List<string>();
        foreach (var seed in seeds)
        {
            var violations = WasmHostCallFuzzTarget.CheckPropertyPath(seed.Bytes, out var http);
            failures.AddRange(violations.Select(v => $"{seed.Name}: {v}"));

            var expected = seed.Single("expect-http");
            if (expected is not null && http != expected)
            {
                failures.Add($"{seed.Name}: expected '{expected}', got '{http ?? "<NotFound>"}'");
            }

            if (seed.Single("expect") == "notfound" && http is not null)
            {
                failures.Add($"{seed.Name}: expected NotFound, got '{http}'");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void PropertyPathCorpus_MutationsNeverThrow()
    {
        var failures = new List<string>();
        var random = new Random(20261102);
        var executed = 0;

        foreach (var seed in FuzzCorpus.Load("proxy-wasm-property-path", ".case"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var violations = WasmHostCallFuzzTarget.CheckPropertyPath(input, out _);
                if (violations.Count > 0)
                {
                    failures.Add($"{seed.Name}: {string.Join("; ", violations)} for input '{Convert.ToHexString(input)}'");
                }
            }
        }

        Assert.IsTrue(executed > 1500, $"mutation pass ran only {executed} inputs");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void EntryPoints_AcceptWellFormedInputAndSwallowMalformedOnes()
    {
        WasmHostCallFuzzTarget.HeaderMap([]);
        WasmHostCallFuzzTarget.HeaderMap([1, 0, 0]);
        WasmHostCallFuzzTarget.HeaderMap([0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0]);
        WasmHostCallFuzzTarget.HeaderMap([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, (byte)'a', 0, (byte)'b', 0]);
        WasmHostCallFuzzTarget.PropertyPath([]);
        WasmHostCallFuzzTarget.PropertyPath(Encoding.UTF8.GetBytes("request\0path\0\0"));
        WasmHostCallFuzzTarget.PropertyPath([0xff, 0xfe, 0]);
    }

    /// <summary>Every proper prefix (sampled for large seeds) plus fixed-seed random edits of a small seed.</summary>
    private static IEnumerable<byte[]> Mutations(byte[] seed, Random random)
    {
        var step = seed.Length > 400 ? Math.Max(1, seed.Length / 60) : 1;
        for (var length = 0; length < seed.Length; length += step)
        {
            yield return seed[..length];
        }

        if (seed.Length == 0 || seed.Length > 4096)
        {
            yield break;
        }

        for (var i = 0; i < MutationsPerSeed; i++)
        {
            var copy = seed.ToList();
            var edits = random.Next(1, 4);
            for (var e = 0; e < edits && copy.Count > 0; e++)
            {
                var at = random.Next(copy.Count);
                switch (random.Next(5))
                {
                    case 0:
                        copy[at] = InterestingBytes[random.Next(InterestingBytes.Length)];
                        break;
                    case 1:
                        copy[at] = (byte)random.Next(256);
                        break;
                    case 2:
                        copy.RemoveAt(at);
                        break;
                    case 3:
                        copy.Insert(at, InterestingBytes[random.Next(InterestingBytes.Length)]);
                        break;
                    default:
                        var take = Math.Min(random.Next(1, 24), copy.Count - at);
                        copy.InsertRange(at, copy.GetRange(at, take));
                        break;
                }
            }

            yield return copy.ToArray();
        }
    }
}
