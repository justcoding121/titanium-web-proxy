using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests.Support;

/// <summary>
///     A small cluster in a <see cref="FakeKubeApi"/>: one GatewayClass owned by this controller, an HTTP Gateway on
///     port 80, one HTTPRoute to a Service with a ready endpoint, and a data-plane Service with two ready pods.
///     The data plane itself is faked: <see cref="FakeObserver"/> reports the hash of whatever ConfigMap was written.
/// </summary>
public sealed class ClusterFixture : IAsyncDisposable
{
    public const string Controller = GatewayTranslatorOptions.DefaultControllerName;
    public const string DataNamespace = "titanium-system";
    public const string DataService = "titanium-data";

    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    public ClusterFixture()
    {
        Api = new FakeKubeApi();
        Observer = new FakeObserver(Api);
        Seed();
    }

    public FakeKubeApi Api { get; }

    public FakeObserver Observer { get; }

    public ClusterCache Cache { get; private set; } = null!;

    public GatewayController Reconciler { get; private set; } = null!;

    public LeaderElector? Leader { get; private set; }

    public static GatewayControllerOptions Options(TimeSpan? minInterval = null) => new()
    {
        Translator = new GatewayTranslatorOptions { DataPlaneServiceNamespace = DataNamespace, DataPlaneServiceName = DataService },
        Output = new OutputOptions { Namespace = DataNamespace, ConfigMapName = DataService, TlsSecretName = DataService + "-tls", DeploymentName = DataService },
        MinReconcileInterval = minInterval ?? TimeSpan.Zero,
        ConfirmPollInterval = TimeSpan.FromMilliseconds(50),
        RetryInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>Creates the cache and controller and fills the cache. Does not start the reconcile loop.</summary>
    public async Task StartCacheAsync(LeaderElector? leader = null, TimeSpan? minInterval = null)
    {
        Leader = leader;
        Cache = new ClusterCache(Api, new ClusterCacheOptions(), NullLogger.Instance);
        var options = Options(minInterval);
        Reconciler = new GatewayController(
            Cache,
            new OutputWriter(Api, options.Output),
            new StatusWriter(Api, Controller, NullLogger.Instance),
            Observer,
            leader,
            options,
            NullLogger.Instance,
            events: new EventWriter(Api, Controller, NullLogger.Instance));
        _running = Cache.RunAsync(_stop.Token);
        await WaitForAsync(() => Cache.HasSynced);
    }

    /// <summary>Starts informers, leader election, and the reconcile loop.</summary>
    public async Task StartLoopAsync(TimeSpan? minInterval = null)
    {
        await StartCacheAsync(null, minInterval);
        _running = Reconciler.RunAsync(_stop.Token);
    }

    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    public string? ConfigText() =>
        Api.Get("configmaps", DataNamespace, DataService)?["data"]?["twp.yaml"]?.GetValue<string>();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_running is not null)
        {
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    public void AddRoute(string name, string host, string service = "web", string ns = "default") =>
        Api.Apply("httproutes", Obj($$"""
            {
              "metadata": { "name": "{{name}}", "namespace": "{{ns}}" },
              "spec": {
                "parentRefs": [ { "name": "gw", "namespace": "default" } ],
                "hostnames": [ "{{host}}" ],
                "rules": [ { "backendRefs": [ { "name": "{{service}}", "port": 8080 } ] } ]
              }
            }
            """));

    public void AddService(string ns, string name, int port, params string[] addresses)
    {
        Api.Apply("services", Obj($$"""
            { "metadata": { "name": "{{name}}", "namespace": "{{ns}}" },
              "spec": { "type": "ClusterIP", "clusterIP": "10.96.1.1", "ports": [ { "port": {{port}}, "targetPort": {{port}} } ] } }
            """));
        var endpoints = string.Join(",", addresses.Select(a => $$"""{ "addresses": [ "{{a}}" ], "conditions": { "ready": true } }"""));
        Api.Apply("endpointslices", Obj($$"""
            { "metadata": { "name": "{{name}}-abc", "namespace": "{{ns}}", "labels": { "kubernetes.io/service-name": "{{name}}" } },
              "addressType": "IPv4",
              "ports": [ { "port": {{port}}, "protocol": "TCP" } ],
              "endpoints": [ {{endpoints}} ] }
            """));
    }

    public static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A kubernetes.io/tls Secret with a fresh self-signed certificate, or garbage when <paramref name="valid"/> is false.</summary>
    public static JsonObject TlsSecret(string ns, string name, bool valid)
    {
        string crt;
        string key;
        if (valid)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=app.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            crt = cert.ExportCertificatePem();
            key = rsa.ExportPkcs8PrivateKeyPem();
        }
        else
        {
            crt = "not a certificate";
            key = "not a key";
        }

        var secret = Obj($$"""{ "metadata": { "name": "{{name}}", "namespace": "{{ns}}" }, "type": "kubernetes.io/tls" }""");
        secret["data"] = new JsonObject
        {
            ["tls.crt"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(crt)),
            ["tls.key"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key)),
        };
        return secret;
    }

    private void Seed()
    {
        foreach (var ns in new[] { "default", DataNamespace })
        {
            Api.Apply("namespaces", Obj($$"""{ "metadata": { "name": "{{ns}}", "labels": { "kubernetes.io/metadata.name": "{{ns}}" } } }"""));
        }

        Api.Apply("gatewayclasses", Obj($$"""{ "metadata": { "name": "titanium" }, "spec": { "controllerName": "{{Controller}}" } }"""));
        Api.Apply("gateways", Obj("""
            {
              "metadata": { "name": "gw", "namespace": "default" },
              "spec": {
                "gatewayClassName": "titanium",
                "listeners": [ { "name": "http", "port": 80, "protocol": "HTTP", "allowedRoutes": { "namespaces": { "from": "All" } } } ]
              }
            }
            """));
        AddService("default", "web", 8080, "10.0.0.1");
        AddRoute("web", "app.test");
        Api.Apply("deployments", Obj($$"""
            { "metadata": { "name": "{{DataService}}", "namespace": "{{DataNamespace}}" },
              "spec": { "template": { "metadata": { "labels": { "app": "data" } } } } }
            """));

        Api.Apply("services", Obj($$"""
            { "metadata": { "name": "{{DataService}}", "namespace": "{{DataNamespace}}" },
              "spec": { "type": "LoadBalancer", "clusterIP": "10.96.0.9", "ports": [ { "name": "http", "port": 80, "targetPort": 8080 } ] },
              "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.10" } ] } } }
            """));
        Api.Apply("endpointslices", Obj($$"""
            { "metadata": { "name": "{{DataService}}-xyz", "namespace": "{{DataNamespace}}", "labels": { "kubernetes.io/service-name": "{{DataService}}" } },
              "addressType": "IPv4",
              "ports": [ { "name": "http", "port": 8080, "protocol": "TCP" } ],
              "endpoints": [ { "addresses": [ "10.1.0.1" ], "conditions": { "ready": true }, "targetRef": { "kind": "Pod", "name": "data-0" } }, { "addresses": [ "10.1.0.2" ], "conditions": { "ready": true }, "targetRef": { "kind": "Pod", "name": "data-1" } } ] }
            """));
    }
}

/// <summary>Stands in for the data-plane pods: every pod runs whatever config the ConfigMap holds, unless told otherwise.</summary>
public sealed class FakeObserver : IDataPlaneObserver
{
    private readonly FakeKubeApi _api;

    public FakeObserver(FakeKubeApi api) => _api = api;

    /// <summary>Addresses that answer with an older hash.</summary>
    public HashSet<string> Stale { get; } = [];

    public int Calls { get; private set; }

    public Task<DataPlaneObservation> ObserveAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        Calls++;
        var text = _api.Get("configmaps", ClusterFixture.DataNamespace, ClusterFixture.DataService)?["data"]?["twp.yaml"]?.GetValue<string>();
        var current = text is null ? null : Convert.ToHexStringLower(SHA256.HashData(new System.Text.UTF8Encoding(false).GetBytes(text)));
        return Task.FromResult(new DataPlaneObservation(addresses.Select(a => Stale.Contains(a) ? "0000" : current).ToList()));
    }
}
