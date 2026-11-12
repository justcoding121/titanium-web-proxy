using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using static Titanium.Gateway.Tests.Support.StateBuilder;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: LeaderElector non-conflict Put failure, ResourceParsers
///     null/metadata edges, BackendTls System CA + SubjectAltNames skip, dual foreign cert Reject append.
/// </summary>
[TestClass]
public class GatewayLeaderParsersAndBackendTlsSoftCoverageBehaviorTests
{
    private sealed class StatusPutApi : IKubeApi
    {
        private readonly IKubeApi _inner;
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.InternalServerError;

        public StatusPutApi(IKubeApi inner) => _inner = inner;

        public async Task<KubeResponse> SendAsync(
            HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken)
        {
            if (method == HttpMethod.Put)
            {
                return new KubeResponse(PutStatus, "put-failed");
            }

            return await _inner.SendAsync(method, pathAndQuery, body, contentType, cancellationToken);
        }

        public Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken) =>
            _inner.OpenWatchAsync(pathAndQuery, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 17, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public async Task LeaderElector_PutNonConflictFailure_IsCaughtByStep()
    {
        var inner = new FakeKubeApi();
        var time = new ManualTime();
        var seed = new LeaderElector(
            inner,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a", Name = "put-fail-lease" },
            NullLogger.Instance,
            time);
        await seed.StepAsync(CancellationToken.None);
        Assert.IsTrue(seed.IsLeader);

        var failing = new StatusPutApi(inner) { PutStatus = HttpStatusCode.InternalServerError };
        var renewer = new LeaderElector(
            failing,
            new LeaderElectionOptions { Namespace = "ns", Identity = "a", Name = "put-fail-lease" },
            NullLogger.Instance,
            time);
        time.Advance(TimeSpan.FromSeconds(2));
        // Non-conflict Put failure throws inside TryAcquireOrRenew and is caught by StepAsync.
        await renewer.StepAsync(CancellationToken.None);
        Assert.IsFalse(renewer.IsLeader);
    }

    [TestMethod]
    public void ResourceParsers_NullMetadataAndEmptyIngress()
    {
        static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        Assert.IsNull(ResourceParsers.Namespace(Parse("""{"metadata":{}}""")));
        Assert.IsNull(ResourceParsers.Service(Parse("""{"metadata":{"name":"svc"}}""")));
        Assert.IsNull(ResourceParsers.EndpointSlice(Parse("""{"metadata":{"name":"s","namespace":"ns"}}""")));
        Assert.IsNull(ResourceParsers.Secret(Parse("""{"metadata":{"name":"tls"}}""")));
        Assert.IsNull(ResourceParsers.ConfigMap(Parse("""{"metadata":{"namespace":"ns"}}""")));

        var ns = ResourceParsers.Namespace(Parse("""{"metadata":{"name":"apps","labels":{"a":null,"b":"x"}}}"""));
        Assert.IsNotNull(ns);
        Assert.AreEqual(string.Empty, ns!.Labels["a"]);
        Assert.AreEqual("x", ns.Labels["b"]);

        var service = ResourceParsers.Service(Parse("""
            {
              "metadata":{"name":"svc","namespace":"ns","annotations":{"k":null}},
              "spec":{"ports":[{"port":80,"targetPort":"http"},{"name":"bad"}]},
              "status":{"loadBalancer":{"ingress":[{"hostname":"lb.example"},{}]}}
            }
            """));
        Assert.IsNotNull(service);
        Assert.AreEqual(1, service!.Ports.Count);
        Assert.AreEqual("http", service.Ports[0].TargetPortName);
        Assert.AreEqual(1, service.LoadBalancerAddresses.Count);
        Assert.IsTrue(service.LoadBalancerAddresses[0].IsHostname);
        Assert.AreEqual(string.Empty, service.Annotations["k"]);
    }

    [TestMethod]
    public void BackendTls_SystemCaAndSubjectAltNames_AndMissingHostname()
    {
        var system = Basic();
        system.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", "web.svc", wellKnownCa: "System"));
        system.Routes.Add(Route("r", "default", null, Rule("web")));
        var systemResult = system.Render(BackendTlsOptions);
        Assert.AreEqual(0, TwpConfigValidator.Validate(systemResult.Config).Count);
        var cluster = systemResult.Config.Clusters.Single(c => c.Id == "default/web:8080");
        Assert.AreEqual("web.svc", cluster.HttpClient!.ServerName);
        Assert.IsNull(cluster.HttpClient.CaBundlePath);
        Assert.IsTrue(cluster.Destinations.All(d => d.UseHttps));

        var san = Basic();
        san.BackendTlsPolicies.Add(BackendTls(
            "pol",
            "default",
            "web",
            "web.svc",
            wellKnownCa: "System",
            subjectAltNames: ["DNS:web.svc"]));
        san.Routes.Add(Route("r", "default", null, Rule("web")));
        var sanResult = san.Render(BackendTlsOptions);
        Assert.IsNull(sanResult.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);

        var noHost = Basic();
        noHost.BackendTlsPolicies.Add(BackendTls("pol", "default", "web", hostname: null, wellKnownCa: "System"));
        noHost.Routes.Add(Route("r", "default", null, Rule("web")));
        var noHostResult = noHost.Render(BackendTlsOptions);
        Assert.IsNull(noHostResult.Config.Clusters.Single(c => c.Id == "default/web:8080").HttpClient);
    }

    [TestMethod]
    public void Https_TwoForeignCertificateKinds_AppendRejectMessages()
    {
        var b = Basic();
        b.Gateways[0].Spec.Listeners =
        [
            new GatewayListener
            {
                Name = "https",
                Port = 443,
                Protocol = "HTTPS",
                Tls = new ListenerTls
                {
                    Mode = "Terminate",
                    CertificateRefs =
                    [
                        new SecretObjectReference { Name = "a", Kind = "ConfigMap" },
                        new SecretObjectReference { Name = "b", Kind = "ConfigMap" },
                    ],
                },
            },
        ];
        var result = b.Render();
        Assert.AreEqual(0, TwpConfigValidator.Validate(result.Config).Count);
        var message = result.StatusPatches.Single(p => p.Kind == StatusKind.Gateway).Listeners!.Single()
            .Conditions.Single(c => c.Type == "ResolvedRefs").Message ?? "";
        StringAssert.Contains(message, "a");
        StringAssert.Contains(message, "b");
    }
}
