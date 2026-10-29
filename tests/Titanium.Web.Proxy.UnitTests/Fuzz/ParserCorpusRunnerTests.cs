using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Ssl;

namespace Titanium.Web.Proxy.UnitTests.Fuzz;

/// <summary>
///     Feeds the adversarial corpus under <c>tests/fuzz/corpus</c> into the HTTP/1 request parser pipeline and
///     <see cref="ClientHelloSniParser" />, then replays a fixed-seed mutation pass over every seed. The contract
///     is the one a request-smuggling defence needs: an ambiguous message is rejected or normalized to a single
///     framing, it is never forwarded as received, and no input makes the parser throw an unexpected exception
///     or hang. A native fuzzer can run the same target - see <c>tests/fuzz/README.md</c>.
/// </summary>
[TestClass]
public class ParserCorpusRunnerTests
{
    private const int MutationsPerSeed = 250;
    private const int MaxMutatedSeedBytes = 4096;
    private const int MaxReportedFailures = 12;

    // The seven ambiguity classes the corpus must keep covering. A missing class fails the guard test.
    private static readonly string[] RequiredHttp1Prefixes =
    [
        "cl-te-", "te-cl-", "dup-cl-", "obs-fold-", "bare-lf-", "space-before-colon-", "oversized-", "invalid-chunk-"
    ];

    private static readonly byte[] InterestingBytes =
        [(byte)'\r', (byte)'\n', 0, (byte)' ', (byte)'\t', (byte)':', (byte)',', (byte)';', (byte)'0', (byte)'f',
            (byte)'-', (byte)'+', 0x0b, 0xff, 0x80];

    [TestMethod]
    public void Http1Corpus_CoversEveryAmbiguityClass()
    {
        var names = FuzzCorpus.Load("http1-request", ".case").Select(c => c.Name).ToList();

        Assert.IsTrue(names.Count >= 40, $"expected a substantial corpus, found {names.Count} seeds");
        foreach (var prefix in RequiredHttp1Prefixes)
            Assert.IsTrue(names.Any(n => n.StartsWith(prefix, StringComparison.Ordinal)),
                $"corpus has no seed for the '{prefix}' class");

        foreach (var c in FuzzCorpus.Load("http1-request", ".case"))
            Assert.IsNotNull(c.Expect, $"{c.Name}: every seed needs an '# expect:' directive");
    }

    [TestMethod]
    public void Http1Corpus_EverySeedMeetsItsExpectation()
    {
        var failures = new List<string>();

        foreach (var seed in FuzzCorpus.Load("http1-request", ".case"))
        {
            var outcome = ParserFuzzTarget.ParseHttp1Request(seed.Bytes);
            failures.AddRange(CheckExpectation(seed, outcome).Select(f => $"{seed.Name}: {f}"));
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void Http1Corpus_MutationsNeverCrashHangOrSlipAmbiguousFramingThrough()
    {
        var failures = new List<string>();
        var random = new Random(20261029);
        var executed = 0;

        foreach (var seed in FuzzCorpus.Load("http1-request", ".case"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var outcome = ParserFuzzTarget.ParseHttp1Request(input);
                if (outcome.Violations.Count > 0)
                    failures.Add($"{seed.Name}: {string.Join("; ", outcome.Violations)} for input " +
                                 $"'{Preview(input)}'");
            }
        }

        Assert.IsTrue(executed > 2000, $"mutation pass ran only {executed} inputs");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void ClientHelloCorpus_EverySeedMeetsItsExpectation()
    {
        var seeds = FuzzCorpus.Load("client-hello", ".hex");
        Assert.IsTrue(seeds.Count >= 10, $"expected a ClientHello corpus, found {seeds.Count} seeds");

        var failures = new List<string>();
        foreach (var seed in seeds)
        {
            var violations = ParserFuzzTarget.CheckClientHello(seed.Bytes);
            failures.AddRange(violations.Select(v => $"{seed.Name}: {v}"));

            var found = ClientHelloSniParser.TryGetServerName(seed.Bytes, out var name, out var result);
            var expect = seed.Expect ?? string.Empty;
            switch (expect)
            {
                case "any":
                    break;
                case "nosni" when result != ClientHelloSniParser.ParseResult.NoSni:
                case "nottls" when result != ClientHelloSniParser.ParseResult.NotTls:
                case "needmore" when result != ClientHelloSniParser.ParseResult.NeedMoreData:
                    failures.Add($"{seed.Name}: expected {expect}, parser said {result}");
                    break;
                default:
                    if (expect.StartsWith("found:", StringComparison.Ordinal) &&
                        (!found || name != expect["found:".Length..]))
                        failures.Add($"{seed.Name}: expected {expect}, parser said {result} '{name}'");
                    break;
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void ClientHelloCorpus_MutationsNeverThrowOrReportABogusName()
    {
        var failures = new List<string>();
        var random = new Random(20261030);
        var executed = 0;

        foreach (var seed in FuzzCorpus.Load("client-hello", ".hex"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var violations = ParserFuzzTarget.CheckClientHello(input);
                if (violations.Count > 0)
                    failures.Add($"{seed.Name}: {string.Join("; ", violations)} for input '{Convert.ToHexString(input)}'");
            }
        }

        Assert.IsTrue(executed > 1000, $"mutation pass ran only {executed} inputs");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void EntryPoints_AcceptWellFormedInputAndSwallowDocumentedRejections()
    {
        ParserFuzzTarget.Http1Request(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: a\r\n\r\n"));
        ParserFuzzTarget.Http1Request(Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n"));
        ParserFuzzTarget.Http1Request([]);
        ParserFuzzTarget.Http1Request([0xff, 0x00, 0x0a]);
        ParserFuzzTarget.ClientHello([]);
        ParserFuzzTarget.ClientHello([0x16, 0x03, 0x01]);
    }

    [TestMethod]
    public void Checker_FlagsAnAmbiguousRequestThatWasForwardedAsReceived()
    {
        // Guards the guard: the invariant checker must actually fail on the shapes the corpus defends against.
        var request = new Titanium.Web.Proxy.Http.Request();
        request.Headers.AddHeader("Content-Length", "4");
        request.Headers.AddHeader("Transfer-Encoding", "chunked");

        var violations = new List<string>();
        ParserFuzzTarget.CheckFramingInvariants(request, violations);

        Assert.IsTrue(violations.Any(v => v.Contains("both Content-Length and Transfer-Encoding")), string.Join("|", violations));

        var folded = new Titanium.Web.Proxy.Http.Request();
        folded.Headers.AddHeader("X-A", "b\r\nContent-Length: 5");
        violations.Clear();
        ParserFuzzTarget.CheckFramingInvariants(folded, violations);

        Assert.IsTrue(violations.Any(v => v.Contains("CR, LF or NUL")), string.Join("|", violations));
    }

    [TestMethod]
    public void CorpusDecoder_ExpandsEscapesAndNestedRepeats()
    {
        CollectionAssert.AreEqual(
            new byte[] { (byte)'a', 13, 10, 9, 0, (byte)'\\', 0x0b, (byte)'z' },
            FuzzCorpus.DecodeEscaped("a\\r\\n\\t\\0\\\\\\x0bz"));

        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("xbbyxbby"),
            FuzzCorpus.DecodeEscaped("%REPEAT(x%REPEAT(b,2)%y,2)%"));

        CollectionAssert.AreEqual(new byte[] { 0x16, 0x03, 0xff }, FuzzCorpus.DecodeHex("16 03\nff"));
        Assert.ThrowsExactly<FormatException>(() => FuzzCorpus.DecodeEscaped("\\q"));
    }

    private static List<string> CheckExpectation(FuzzCase seed, Http1ParseOutcome outcome)
    {
        var problems = new List<string>(outcome.Violations);
        var expect = seed.Expect;

        switch (expect)
        {
            case "reject":
                if (!outcome.Rejected)
                    problems.Add($"expected a rejection but the request was accepted (bodyComplete={outcome.BodyComplete})");
                else if (seed.Single("reject-type") is { } type && outcome.ExceptionType != type)
                    problems.Add($"expected rejection by {type} but got {outcome.ExceptionType}");
                break;

            case "incomplete":
                if (!outcome.Rejected && outcome.BodyComplete)
                    problems.Add("a truncated body was relayed as a complete message");
                break;

            case "normalize":
            case "accept":
                if (outcome.Rejected)
                    problems.Add($"expected the request to be accepted but {outcome.ExceptionType} rejected it");
                else
                    problems.AddRange(CheckAssertions(seed, outcome));
                break;

            default:
                problems.Add($"unknown expect value '{expect}'");
                break;
        }

        return problems;
    }

    private static IEnumerable<string> CheckAssertions(FuzzCase seed, Http1ParseOutcome outcome)
    {
        var headers = outcome.Request!.Headers;

        foreach (var assertion in seed.All("assert-header"))
        {
            var (name, value) = SplitPair(assertion);
            var actual = headers.GetFirstHeader(name)?.Value;
            if (actual != value)
                yield return $"expected header {name}='{value}', found '{actual ?? "<absent>"}'";
        }

        foreach (var name in seed.All("assert-no-header"))
        {
            if (headers.HeaderExists(name))
                yield return $"header {name} must not be forwarded";
        }

        foreach (var assertion in seed.All("assert-header-count"))
        {
            var (name, count) = SplitPair(assertion);
            var actual = headers.GetHeaders(name)?.Count ?? 0;
            if (actual != int.Parse(count, System.Globalization.CultureInfo.InvariantCulture))
                yield return $"expected {count} {name} field(s), found {actual}";
        }

        if (seed.Directives.Any(d => d.Key == "assert-body-complete") && !outcome.BodyComplete)
            yield return "expected the body to be relayed completely";
    }

    private static (string Name, string Value) SplitPair(string assertion)
    {
        var eq = assertion.IndexOf('=');
        return (assertion[..eq].Trim(), assertion[(eq + 1)..].Trim());
    }

    /// <summary>Every proper prefix (sampled for large seeds) plus fixed-seed random edits of a small seed.</summary>
    private static IEnumerable<byte[]> Mutations(byte[] seed, Random random)
    {
        var step = seed.Length > 400 ? Math.Max(1, seed.Length / 60) : 1;
        for (var length = 0; length < seed.Length; length += step)
            yield return seed[..length];

        if (seed.Length == 0 || seed.Length > MaxMutatedSeedBytes) yield break;

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

    private static string Preview(byte[] input)
    {
        var text = ParserFuzzTarget.Escape(Encoding.Latin1.GetString(input));
        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
