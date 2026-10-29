using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Validation;
using Titanium.Web.Proxy.UnitTests.Fuzz;

namespace Titanium.Manager.Tests.Fuzz;

/// <summary>
/// Deterministic corpus + mutation pass for manager revision YAML and agent ack JSON.
/// See <c>tests/fuzz/README.md</c>.
/// </summary>
[TestClass]
public class ManagerPayloadCorpusTests
{
    private const int MutationsPerSeed = 200;
    private const int MaxReportedFailures = 12;

    private static readonly byte[] InterestingBytes =
        [0, (byte)'{', (byte)'}', (byte)'[', (byte)']', (byte)'"', (byte)':', (byte)',',
            (byte)'\n', (byte)'\r', 0xff, 0x80, (byte)'\\', (byte)'x'];

    [TestMethod]
    public void RevisionYamlCorpus_EverySeedMeetsExpectation()
    {
        var seeds = FuzzCorpus.Load("manager-revision-yaml", ".case");
        Assert.IsTrue(seeds.Count >= 4, $"expected revision-yaml corpus, found {seeds.Count}");
        var failures = new List<string>();
        foreach (var seed in seeds)
        {
            Assert.IsNotNull(seed.Expect, $"{seed.Name}: needs # expect:");
            var errors = ConfigRevisionValidator.ValidateYaml(Encoding.UTF8.GetString(seed.Bytes));
            var violations = RevisionYamlFuzzTarget.Check(seed.Bytes);
            failures.AddRange(violations.Select(v => $"{seed.Name}: {v}"));

            var expect = seed.Expect ?? "";
            if (expect == "accept" && errors.Count > 0)
            {
                failures.Add($"{seed.Name}: expected accept, got {string.Join("; ", errors)}");
            }
            else if (expect == "reject" && errors.Count == 0)
            {
                failures.Add($"{seed.Name}: expected reject, validator accepted");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(MaxReportedFailures)));
    }

    [TestMethod]
    public void RevisionYamlCorpus_MutationsNeverThrow()
    {
        var failures = new List<string>();
        var random = new Random(20261029);
        var executed = 0;
        foreach (var seed in FuzzCorpus.Load("manager-revision-yaml", ".case"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var violations = RevisionYamlFuzzTarget.Check(input);
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
    public void AgentAckCorpus_EverySeedMeetsExpectation()
    {
        var seeds = FuzzCorpus.Load("manager-agent-ack", ".case");
        Assert.IsTrue(seeds.Count >= 4, $"expected agent-ack corpus, found {seeds.Count}");
        var failures = new List<string>();
        foreach (var seed in seeds)
        {
            Assert.IsNotNull(seed.Expect, $"{seed.Name}: needs # expect:");
            var ok = AgentAckPayload.TryParse(seed.Bytes, out _, out _);
            var violations = AgentAckPayload.Check(seed.Bytes);
            failures.AddRange(violations.Select(v => $"{seed.Name}: {v}"));

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
    public void AgentAckCorpus_MutationsNeverThrow()
    {
        var failures = new List<string>();
        var random = new Random(20261030);
        var executed = 0;
        foreach (var seed in FuzzCorpus.Load("manager-agent-ack", ".case"))
        {
            foreach (var input in Mutations(seed.Bytes, random))
            {
                executed++;
                var violations = AgentAckPayload.Check(input);
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
    public void FuzzEntryPoints_AcceptEmptyAndHostile()
    {
        RevisionYamlFuzzTarget.Yaml([]);
        RevisionYamlFuzzTarget.Yaml(Encoding.UTF8.GetBytes("listeners: []\n"));
        AgentAckPayload.Fuzz([]);
        AgentAckPayload.Fuzz(Encoding.UTF8.GetBytes("{\"ok\":true,\"node_id\":\"n\",\"config_hash\":\"h\"}"));
        AgentAckPayload.Fuzz([0xff, 0xfe, (byte)'{']);
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
                buf = [InterestingBytes[random.Next(InterestingBytes.Length)]];
                yield return buf;
                continue;
            }

            var kind = random.Next(4);
            var at = random.Next(buf.Length);
            switch (kind)
            {
                case 0:
                    buf[at] = InterestingBytes[random.Next(InterestingBytes.Length)];
                    break;
                case 1 when buf.Length > 1:
                    var list = buf.ToList();
                    list.RemoveAt(at);
                    buf = list.ToArray();
                    break;
                case 2:
                    var insert = buf.ToList();
                    insert.Insert(at, InterestingBytes[random.Next(InterestingBytes.Length)]);
                    buf = insert.ToArray();
                    break;
                default:
                    buf[at] = (byte)random.Next(256);
                    break;
            }

            yield return buf;
        }
    }
}
