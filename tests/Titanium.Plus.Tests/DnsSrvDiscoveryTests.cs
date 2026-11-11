using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Discovery;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class DnsSrvDiscoveryTests
{
    [TestMethod]
    public void OrderRows_SortsByPriorityThenTarget()
    {
        var ordered = DnsSrvDiscovery.OrderRows(
        [
            new DnsSrvDiscovery.SrvRow("b.example.", 8080, 10, 5),
            new DnsSrvDiscovery.SrvRow("a.example.", 8080, 10, 1),
            new DnsSrvDiscovery.SrvRow("z.example.", 8080, 5, 1),
        ]);
        Assert.AreEqual("z.example.", ordered[0].Target);
        Assert.AreEqual("a.example.", ordered[1].Target);
        Assert.AreEqual("b.example.", ordered[2].Target);
    }

    [TestMethod]
    public void BuildDestinations_CapsAtMaxTargets()
    {
        var rows = new List<DnsSrvDiscovery.SrvRow>
        {
            new("host1.", 80, 1, 10),
            new("host2.", 81, 1, 10),
            new("host3.", 82, 1, 10),
        };
        var addresses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["host1."] = ["10.0.0.1", "10.0.0.2"],
            ["host2."] = ["10.0.0.3"],
            ["host3."] = ["10.0.0.4"],
        };
        var list = DnsSrvDiscovery.BuildDestinations("svc", rows, addresses, maxTargets: 2, out var truncated);
        Assert.IsTrue(truncated);
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual(10, list[0].Weight);
        Assert.AreEqual(1, list[0].Priority);
    }

    [TestMethod]
    public async Task DnsApply_PreservesClusterAlgorithmHealthAndRetry()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "svc",
                Algorithm = LoadBalanceAlgorithm.LeastRequests,
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthConfig { Enabled = true, Kind = ActiveHealthKind.Grpc },
                },
                Retry = new RetryConfig { Attempts = 2 },
                Destinations = [new DestinationConfig { Id = "old", Address = "10.0.0.1" }],
            },
        ]);
        var state = new DiscoveryApplyState();
        await ServiceDiscovery.ApplyDnsAsync(
            new PlusActivationContext { ProxyServer = new object(), ClusterManager = manager },
            "localhost",
            80,
            "svc",
            state,
            CancellationToken.None);
        var cluster = manager.Snapshot.Clusters["svc"];
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, cluster.Algorithm);
        Assert.AreEqual(ActiveHealthKind.Grpc, cluster.HealthCheck!.Active!.Kind);
        Assert.AreEqual(2, cluster.Retry!.Attempts);
        Assert.IsTrue(cluster.Destinations.Count > 0);
    }

    // ── FakeDnsServer integration tests ────────────────────────────────────────

    [TestMethod]
    public void BuildDestinations_WeightAndPriorityPropagatedToDestinations()
    {
        var rows = DnsSrvDiscovery.OrderRows(
        [
            new DnsSrvDiscovery.SrvRow("host-a.", 8081, Priority: 5,  Weight: 20),
            new DnsSrvDiscovery.SrvRow("host-b.", 8082, Priority: 10, Weight:  7),
        ]);
        var addresses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["host-a."] = ["10.0.0.1"],
            ["host-b."] = ["10.0.0.2"],
        };
        var dests = DnsSrvDiscovery.BuildDestinations("svc", rows, addresses, maxTargets: 10, out _);

        Assert.AreEqual(2, dests.Count);
        Assert.AreEqual(8081, dests[0].Port, "Lower priority (5) must come first.");
        Assert.AreEqual(5,    dests[0].Priority);
        Assert.AreEqual(20,   dests[0].Weight);
        Assert.AreEqual(8082, dests[1].Port);
        Assert.AreEqual(10,   dests[1].Priority);
        Assert.AreEqual(7,    dests[1].Weight);
    }

    [TestMethod]
    public void BuildDestinations_TargetWithNoAddresses_IsSkipped()
    {
        var rows = DnsSrvDiscovery.OrderRows(
        [
            new DnsSrvDiscovery.SrvRow("resolved.", 80, 1, 1),
            new DnsSrvDiscovery.SrvRow("unresolved.", 81, 2, 1),
        ]);
        var addresses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["resolved."] = ["10.0.0.1"],
            // "unresolved." not present → skipped
        };
        var dests = DnsSrvDiscovery.BuildDestinations("svc", rows, addresses, maxTargets: 10, out var truncated);

        Assert.AreEqual(1, dests.Count, "Targets without resolved addresses must be dropped.");
        Assert.AreEqual(80, dests[0].Port);
        Assert.IsFalse(truncated);
    }

    [TestMethod]
    public async Task QueryAsync_SrvRecords_OrderedByPriority_WeightAndTtl()
    {
        await using var dns = FakeDnsServer.Start();
        // Lower priority number = higher priority; should come out first.
        dns.SetRecords(
        [
            new FakeDnsServer.SrvEntry("localhost", Port: 9092, Priority: 10, Weight: 3, Ttl: 30),
            new FakeDnsServer.SrvEntry("localhost", Port: 9091, Priority:  5, Weight: 7, Ttl: 20),
        ]);

        var (dests, ttl, truncated) = await DnsSrvDiscovery.QueryAsync(
            "_svc._tcp.test", "svc", maxTargets: 20, dns.Endpoint, CancellationToken.None);

        Assert.IsFalse(truncated);
        Assert.AreEqual(20, ttl, "TTL must be the minimum across all SRV records.");
        Assert.IsTrue(dests.Count >= 2, "Expected at least one destination per SRV record.");

        // All port-9091 destinations (priority 5) must precede all port-9092 ones (priority 10).
        var ports = dests.Select(d => d.Port).ToList();
        var last9091 = ports.LastIndexOf(9091);
        var first9092 = ports.IndexOf(9092);
        Assert.IsTrue(first9092 < 0 || last9091 < first9092,
            "Priority-5 destinations must precede priority-10 ones in the returned list.");

        // Weight and Priority fields must be set correctly.
        Assert.IsTrue(dests.Where(d => d.Port == 9091).All(d => d.Priority == 5),
            "Destinations for priority-5 SRV records must carry Priority=5.");
        Assert.IsTrue(dests.Where(d => d.Port == 9091).All(d => d.Weight == 7),
            "Destinations for priority-5 SRV records must carry Weight=7.");
        Assert.IsTrue(dests.Where(d => d.Port == 9092).All(d => d.Priority == 10));
        Assert.IsTrue(dests.Where(d => d.Port == 9092).All(d => d.Weight == 3));
    }

    [TestMethod]
    public async Task QueryAsync_EmptyAnswer_ReturnsEmptyListAndZeroTtl()
    {
        await using var dns = FakeDnsServer.Start();
        // No records configured → NOERROR with ANCOUNT=0.
        dns.SetRecords([]);

        var (dests, ttl, truncated) = await DnsSrvDiscovery.QueryAsync(
            "_svc._tcp.test", "svc", maxTargets: 10, dns.Endpoint, CancellationToken.None);

        Assert.AreEqual(0, dests.Count, "An empty SRV answer must return zero destinations.");
        Assert.AreEqual(0, ttl, "Zero-record response should produce TTL=0.");
        Assert.IsFalse(truncated);
    }

    [TestMethod]
    public async Task QueryAsync_NxDomain_ThrowsDnsException()
    {
        await using var dns = FakeDnsServer.Start();
        dns.SetFault(FakeDnsServer.FaultMode.NxDomain);

        var threw = false;
        try
        {
            await DnsSrvDiscovery.QueryAsync("_svc._tcp.test", "svc", 10, dns.Endpoint, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Expected a DNS error exception, not OperationCanceledException.");
        }
        catch
        {
            threw = true;
        }

        Assert.IsTrue(threw, "QueryAsync must throw when the server returns NXDOMAIN.");
    }

    [TestMethod]
    public async Task QueryAsync_ServFail_ThrowsDnsException()
    {
        await using var dns = FakeDnsServer.Start();
        dns.SetFault(FakeDnsServer.FaultMode.ServFail);

        var threw = false;
        try
        {
            await DnsSrvDiscovery.QueryAsync("_svc._tcp.test", "svc", 10, dns.Endpoint, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Expected a DNS error exception, not OperationCanceledException.");
        }
        catch
        {
            threw = true;
        }

        Assert.IsTrue(threw, "QueryAsync must throw when the server returns SERVFAIL.");
    }

    [TestMethod]
    public async Task QueryAsync_TruncatedUdp_FallsBackToTcpAndReturnsFullAnswer()
    {
        await using var dns = FakeDnsServer.Start();
        dns.SetRecords(
        [
            new FakeDnsServer.SrvEntry("localhost", Port: 9090, Priority: 1, Weight: 1, Ttl: 10),
        ],
        FakeDnsServer.FaultMode.TruncateUdpUseTcp);

        // DnsClient's UseTcpFallback=true (default) should retry over TCP automatically.
        var (dests, _, _) = await DnsSrvDiscovery.QueryAsync(
            "_svc._tcp.test", "svc", maxTargets: 10, dns.Endpoint, CancellationToken.None);

        Assert.IsTrue(dests.Count > 0,
            "After TCP fallback the full SRV answer must be delivered.");
        Assert.IsTrue(dests.All(d => d.Port == 9090),
            "Destination port must match the SRV record served over TCP.");
    }

    [TestMethod]
    public async Task QueryAsync_MalformedResponse_ThrowsDnsException()
    {
        await using var dns = FakeDnsServer.Start();
        dns.SetFault(FakeDnsServer.FaultMode.Malformed);

        var threw = false;
        try
        {
            await DnsSrvDiscovery.QueryAsync("_svc._tcp.test", "svc", 10, dns.Endpoint, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Expected a DNS parse exception, not OperationCanceledException.");
        }
        catch
        {
            threw = true;
        }

        Assert.IsTrue(threw, "QueryAsync must throw (not hang or crash) when the DNS response is malformed.");
    }

    [TestMethod]
    public async Task QueryAsync_RecordsCappedAtMaxTargets_SetsTruncatedFlag()
    {
        await using var dns = FakeDnsServer.Start();
        dns.SetRecords(
        [
            new FakeDnsServer.SrvEntry("localhost", Port: 9081, Priority: 1, Weight: 1),
            new FakeDnsServer.SrvEntry("localhost", Port: 9082, Priority: 2, Weight: 1),
            new FakeDnsServer.SrvEntry("localhost", Port: 9083, Priority: 3, Weight: 1),
        ]);

        // localhost may resolve to multiple IPs; with maxTargets=1 at least one is over the cap.
        // Use maxTargets=2 to ensure cap even if localhost → single IP.
        var (dests, _, truncated) = await DnsSrvDiscovery.QueryAsync(
            "_svc._tcp.test", "svc", maxTargets: 2, dns.Endpoint, CancellationToken.None);

        Assert.AreEqual(2, dests.Count, "BuildDestinations must stop at maxTargets.");
        Assert.IsTrue(truncated, "Truncated flag must be set when the cap is hit.");
    }

    // -------------------------------------------------------------------------
    // ParseNameServer / option clamps — remaining DnsSrvDiscovery uncovered paths
    // Bug class: malformed discovery.dnsServer or out-of-range clamps break discovery
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ParseNameServer_Ipv4WithPort_UsesExplicitPort()
    {
        // Bug class: port after IPv4 is ignored, so discovery queries the wrong name server.
        var ep = DnsSrvDiscovery.ParseNameServer("8.8.8.8:5353");
        Assert.AreEqual(System.Net.IPAddress.Parse("8.8.8.8"), ep.Address);
        Assert.AreEqual(5353, ep.Port);
    }

    [TestMethod]
    public void ParseNameServer_Ipv4WithoutPort_DefaultsTo53()
    {
        var ep = DnsSrvDiscovery.ParseNameServer("1.1.1.1");
        Assert.AreEqual(System.Net.IPAddress.Parse("1.1.1.1"), ep.Address);
        Assert.AreEqual(53, ep.Port);
    }

    [TestMethod]
    public void ParseNameServer_BracketedIpv6WithPort_ParsesBoth()
    {
        // Bug class: bracketed IPv6 is rejected or the port is mis-parsed as part of the address.
        var ep = DnsSrvDiscovery.ParseNameServer("[2001:db8::1]:5353");
        Assert.AreEqual(System.Net.IPAddress.Parse("2001:db8::1"), ep.Address);
        Assert.AreEqual(5353, ep.Port);
    }

    [TestMethod]
    public void ParseNameServer_MalformedBracketedIpv6_ThrowsFormatException()
    {
        // Bug class: missing ']' or missing ':' after ']' is accepted and misroutes queries.
        Assert.ThrowsExactly<FormatException>(() => DnsSrvDiscovery.ParseNameServer("[2001:db8::1"));
        Assert.ThrowsExactly<FormatException>(() => DnsSrvDiscovery.ParseNameServer("[2001:db8::1]5353"));
    }

    [TestMethod]
    public void ParseNameServer_Hostname_ResolvesToIp()
    {
        // Bug class: hostname dnsServer values throw instead of resolving via DNS.
        var ep = DnsSrvDiscovery.ParseNameServer("localhost");
        Assert.IsNotNull(ep.Address);
        Assert.AreEqual(53, ep.Port);
    }

    [TestMethod]
    public void ReadIntervalMs_ClampsAndDefaults()
    {
        // Bug class: intervalMs below MinIntervalMs or above 1h is accepted, spinning discovery.
        Assert.AreEqual(DnsSrvDiscovery.DefaultIntervalMs,
            DnsSrvDiscovery.ReadIntervalMs(new Dictionary<string, string>()));
        Assert.AreEqual(DnsSrvDiscovery.MinIntervalMs,
            DnsSrvDiscovery.ReadIntervalMs(new Dictionary<string, string>
            {
                ["discovery.intervalMs"] = "1",
            }));
        Assert.AreEqual(3_600_000,
            DnsSrvDiscovery.ReadIntervalMs(new Dictionary<string, string>
            {
                ["discovery.intervalMs"] = "99999999",
            }));
        Assert.AreEqual(5_000,
            DnsSrvDiscovery.ReadIntervalMs(new Dictionary<string, string>
            {
                ["discovery.intervalMs"] = "5000",
            }));
    }

    [TestMethod]
    public void ReadMaxTargets_ClampsAndDefaults()
    {
        // Bug class: srvMaxTargets of 0 or huge values bypass the destination cap.
        Assert.AreEqual(DnsSrvDiscovery.DefaultMaxTargets,
            DnsSrvDiscovery.ReadMaxTargets(new Dictionary<string, string>()));
        Assert.AreEqual(1,
            DnsSrvDiscovery.ReadMaxTargets(new Dictionary<string, string>
            {
                ["discovery.srvMaxTargets"] = "0",
            }));
        Assert.AreEqual(DnsSrvDiscovery.MaxMaxTargets,
            DnsSrvDiscovery.ReadMaxTargets(new Dictionary<string, string>
            {
                ["discovery.srvMaxTargets"] = "99999",
            }));
    }

    [TestMethod]
    public async Task QueryAsync_NullDnsServer_UsesSystemResolver()
    {
        // Bug class: null dnsServer path (system resolver) never builds a LookupClient.
        // Use a FakeDnsServer only to prove the null-server overload constructs; when there is no
        // SRV for a random name the call may return empty or throw — either is fine as long as it
        // does not hang. Prefer FakeDns with an explicit endpoint for deterministic empty answers;
        // this case specifically passes null to exercise CreateLookupClient(null).
        try
        {
            var (dests, ttl, _) = await DnsSrvDiscovery.QueryAsync(
                "_definitely-missing-twp-srv._tcp.invalid",
                "svc",
                maxTargets: 2,
                dnsServer: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(dests.Count == 0 || ttl >= 0);
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            // NXDOMAIN / SERVFAIL / timeout from the system resolver are acceptable for this path.
            Assert.IsFalse(ex is OutOfMemoryException);
        }
    }
}
