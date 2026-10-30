using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Manager;
using Titanium.Web.Proxy.UnitTests.Fuzz;

namespace Titanium.Plus.Tests.Fuzz;

/// <summary>
/// Deterministic corpus + mutation pass for manager→agent config JSON.
/// See <c>tests/fuzz/README.md</c>.
/// </summary>
[TestClass]
public class AgentConfigCorpusTests
{
    private const int MutationsPerSeed = 200;
    private const int MaxReportedFailures = 12;

    private static readonly byte[] InterestingBytes =
        [0, (byte)'{', (byte)'}', (byte)'"', (byte)':', (byte)',', (byte)'\n', 0xff, 0x80];

    [TestMethod]
    public void AgentConfigCorpus_EverySeedMeetsExpectation()
    {
        var seeds = FuzzCorpus.Load("manager-agent-config", ".case");
        Assert.IsTrue(seeds.Count >= 4, $"expected agent-config corpus, found {seeds.Count}");
        var failures = new List<string>();
        foreach (var seed in seeds)
        {
            Assert.IsNotNull(seed.Expect, $"{seed.Name}: needs # expect:");
            var ok = AgentConfigPayload.TryParse(seed.Bytes, out _, out _);
            failures.AddRange(AgentConfigPayload.Check(seed.Bytes).Select(v => $"{seed.Name}: {v}"));

            var expect = seed.Expect ?? "";
            if (expect == "accept" && !ok)
            {
                failures.Add($"{seed.Name}: expected accept");
            }
            else if (expect == "reject" && ok)
            {
                failures.Add($"{seed.Name}: expected reject");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void AgentConfigCorpus_MutationsNeverThrow()
    {
        var failures = new List<string>();
        var random = new Random(20261031);
        var executed = 0;
        foreach (var seed in FuzzCorpus.Load("manager-agent-config", ".case"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var violations = AgentConfigPayload.Check(input);
                if (violations.Count > 0)
                {
                    failures.Add($"{seed.Name}: {string.Join("; ", violations)}");
                }
            }
        }

        Assert.IsTrue(executed > 200, $"mutation pass ran only {executed}");
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void FuzzEntryPoint_AcceptEmptyAndHostile()
    {
        AgentConfigPayload.Fuzz([]);
        AgentConfigPayload.Fuzz(Encoding.UTF8.GetBytes(
            """{"configHash":"h","yaml":"listeners: []\n"}"""));
        AgentConfigPayload.Fuzz([0xff, (byte)'{', 0]);
        Assert.AreEqual(0, AgentConfigPayload.Check([0xff, (byte)'{', 0]).Count);
    }

    private static IEnumerable<byte[]> Mutations(byte[] seed, Random random)
    {
        var capped = seed.Length > 2048 ? seed.AsSpan(0, 2048).ToArray() : seed;
        for (var i = 0; i <= capped.Length; i += Math.Max(1, capped.Length / 16))
        {
            yield return capped.AsSpan(0, i).ToArray();
        }

        for (var m = 0; m < MutationsPerSeed; m++)
        {
            var buf = (byte[])capped.Clone();
            if (buf.Length == 0)
            {
                yield return [InterestingBytes[random.Next(InterestingBytes.Length)]];
                continue;
            }

            var at = random.Next(buf.Length);
            buf[at] = InterestingBytes[random.Next(InterestingBytes.Length)];
            yield return buf;
        }
    }
}
