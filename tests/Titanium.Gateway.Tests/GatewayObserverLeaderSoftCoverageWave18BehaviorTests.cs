using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: HttpDataPlaneObserver IPv6 and bad version payloads,
///     LeaderElector ParseTime on renew, Put hard failure, and Ingress annotation rejects.
/// </summary>
[TestClass]
public class GatewayObserverLeaderSoftCoverageWave18BehaviorTests
{
    private sealed class ObserverHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Responder is not null)
            {
                return Task.FromResult(Responder(request));
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/readyz")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"config_hash":"wave18"}"""),
            });
        }
    }

    private sealed class FailingPutApi : IKubeApi
    {
        private readonly IKubeApi _inner;

        public FailingPutApi(IKubeApi inner) => _inner = inner;

        public Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Put && pathAndQuery.Contains("/leases/", StringComparison.Ordinal))
            {
                return Task.FromResult(new KubeResponse(HttpStatusCode.InternalServerError, "put-hard-fail"));
            }

            return _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 16, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task Observer_Ipv6Address_UsesBracketedAdminUri()
    {
        var handler = new ObserverHandler();
        var observer = new HttpDataPlaneObserver(new HttpClient(handler), 9901, null, NullLogger.Instance);

        _ = await observer.ObserveAsync(["2001:db8::1"], CancellationToken.None);

        var uri = handler.Requests[0].RequestUri!.ToString();
        StringAssert.Contains(uri, "[2001:db8::1]");
        StringAssert.Contains(uri, ":9901/readyz");
    }

    [TestMethod]
    public async Task Observer_InvalidVersionJson_AndMissingHash_ReturnNull()
    {
        var handler = new ObserverHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath switch
            {
                "/readyz" => new HttpResponseMessage(HttpStatusCode.OK),
                "/version" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
        var badJson = await new HttpDataPlaneObserver(new HttpClient(handler), 9901, null, NullLogger.Instance)
            .ObserveAsync(["10.9.0.1"], CancellationToken.None);
        Assert.IsNull(badJson.PodHashes[0]);

        handler = new ObserverHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath switch
            {
                "/readyz" => new HttpResponseMessage(HttpStatusCode.OK),
                "/version" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"version":"1"}""") },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
        var noHash = await new HttpDataPlaneObserver(new HttpClient(handler), 9901, null, NullLogger.Instance)
            .ObserveAsync(["10.9.0.2"], CancellationToken.None);
        Assert.IsNull(noHash.PodHashes[0]);
    }

    [TestMethod]
    public async Task Observer_CapsProbeCount_AtSixtyFourPods()
    {
        var handler = new ObserverHandler();
        var observer = new HttpDataPlaneObserver(new HttpClient(handler), 9901, "tok", NullLogger.Instance);
        var addresses = Enumerable.Range(1, 70).Select(i => $"10.8.0.{i}").ToList();

        var observation = await observer.ObserveAsync(addresses, CancellationToken.None);

        Assert.AreEqual(64, observation.PodHashes.Count);
        Assert.AreEqual(128, handler.Requests.Count);
        Assert.IsTrue(observation.Confirms("wave18"));
    }

    [TestMethod]
    public async Task LeaderElector_RenewWithBadAcquireTime_StillLeads()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "wave18" },
            NullLogger.Instance,
            time);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);

        var lease = api.Get("leases", "ns", "titanium-gateway")!;
        lease["spec"]!["acquireTime"] = "not-a-time";
        api.Apply("leases", lease);
        time.Advance(TimeSpan.FromSeconds(2));
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);
    }

    [TestMethod]
    public async Task LeaderElector_RenewPutFailure_IsToleratedUntilRenewDeadline()
    {
        var api = new FailingPutApi(new FakeKubeApi());
        var time = new ManualTime();
        var elector = new LeaderElector(
            api,
            new LeaderElectionOptions { Namespace = "ns", Identity = "put-fail" },
            NullLogger.Instance,
            time);
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader);

        time.Advance(TimeSpan.FromSeconds(1));
        await elector.StepAsync(CancellationToken.None);
        Assert.IsTrue(elector.IsLeader, "a single failed renew inside the deadline should not step down");
    }

    [TestMethod]
    public void IngressAnnotations_SslRedirectTrue_AndLimitRpsZero_AreRejected()
    {
        var ssl = IngressAnnotations.Parse(
            new Dictionary<string, string> { [IngressAnnotations.Prefix + "ssl-redirect"] = "true" },
            "default");
        Assert.IsTrue(ssl.Problems.Exists(p => p.Contains("ssl-redirect", StringComparison.OrdinalIgnoreCase)));

        var rps = IngressAnnotations.Parse(
            new Dictionary<string, string> { [IngressAnnotations.Prefix + "limit-rps"] = "0" },
            "default");
        Assert.IsTrue(rps.Problems.Count > 0);
    }

    [TestMethod]
    public void DataPlaneObservation_None_DoesNotConfirmHash()
    {
        Assert.IsFalse(DataPlaneObservation.None.Confirms("wave18"));
    }
}
