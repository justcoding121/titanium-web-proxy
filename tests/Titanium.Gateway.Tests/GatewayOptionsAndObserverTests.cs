using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;

namespace Titanium.Gateway.Tests;

[TestClass]
public sealed class GatewayOptionsAndObserverTests
{
    private static readonly Dictionary<string, string?> NoEnvironment = [];
    private static readonly string[] SecretNamespacesAb = ["a", "b"];
    private static readonly string[] IgnoredKubernetesServiceEnv =
    [
        "TITANIUM_GATEWAY_DATA_PORT", "TITANIUM_GATEWAY_DATA_SERVICE_PORT_HTTPS",
    ];

    [TestMethod]
    public void Options_ReadFlagsAndFallBackToThePodEnvironment()
    {
        var environment = new Dictionary<string, string?> { ["POD_NAMESPACE"] = "gw-system", ["POD_NAME"] = "gw-0" };

        var options = GatewayOptions.Parse(["--data-plane-service", "titanium", "--port-map", "80=8080,8443=9443"], environment);

        Assert.AreEqual("gw-system", options.DataPlaneNamespace);
        Assert.AreEqual("titanium", options.ConfigMapName);
        Assert.AreEqual("titanium-tls", options.TlsSecretName);
        Assert.AreEqual("gw-0", options.Identity);
        Assert.AreEqual(9443, options.PortMap[8443]);
        Assert.IsTrue(options.LeaderElection);
        Assert.AreEqual("/etc/titanium", options.TlsMountPath);
        Assert.IsNull(options.ToControllerOptions().Output.DeploymentName);
        Assert.AreEqual("titaniumproxy.com/gateway-controller", options.ToControllerOptions().Translator.ControllerName);
    }

    [TestMethod]
    public void Options_ReadTheChartEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium",
            ["TITANIUM_GATEWAY_DATA_PLANE_NAMESPACE"] = "data",
            ["TITANIUM_GATEWAY_SECRET_NAMESPACES"] = "a, b",
            ["TITANIUM_GATEWAY_NO_LEADER_ELECTION"] = "true",
        };

        var options = GatewayOptions.Parse([], environment);

        Assert.AreEqual("data", options.DataPlaneNamespace);
        CollectionAssert.AreEqual(SecretNamespacesAb, options.SecretNamespaces.ToArray());
        Assert.IsFalse(options.LeaderElection);
    }

    [TestMethod]
    [DataRow("--bogus")]
    [DataRow("positional")]
    [DataRow("--data-plane-service")]
    public void Options_RejectBadArguments(string argument)
    {
        Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse([argument], NoEnvironment));
    }

    [TestMethod]
    public void Options_RejectUnknownFlags_AndReportUnknownEnvironmentNames()
    {
        var flag = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse(["--bogus", "x"], NoEnvironment));
        StringAssert.Contains(flag.Message, "bogus");
        // Service-link variables Kubernetes injects for a Service named titanium-gateway-data share the prefix.
        var env = new Dictionary<string, string?>
        {
            ["TITANIUM_GATEWAY_DATA_PORT"] = "tcp://10.96.0.1:80",
            ["TITANIUM_GATEWAY_DATA_SERVICE_PORT_HTTPS"] = "443",
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium",
        };
        var options = GatewayOptions.Parse(["--no-leader-election", "--data-plane-namespace", "x"], env);
        Assert.AreEqual("titanium", options.DataPlaneService);
        CollectionAssert.AreEqual(IgnoredKubernetesServiceEnv, options.IgnoredEnvironment.ToArray());
    }

    [TestMethod]
    public void IngressOptions_DefaultToOurControllerNameAndNoLegacyAnnotation()
    {
        var options = GatewayOptions.Parse(["--data-plane-service", "titanium"], new Dictionary<string, string?> { ["POD_NAMESPACE"] = "gw" });

        Assert.AreEqual("titanium.io/ingress-controller", options.IngressControllerName);
        Assert.IsFalse(options.WatchLegacyAnnotation);
        var translator = options.ToControllerOptions().Translator;
        Assert.AreEqual("titanium.io/ingress-controller", translator.IngressControllerName);
        Assert.IsFalse(translator.WatchLegacyAnnotation);
    }

    [TestMethod]
    public void IngressOptions_AreReadFromFlags_AndTheLegacyFlagTakesNoValue()
    {
        var options = GatewayOptions.Parse(
            ["--data-plane-service", "titanium", "--watch-legacy-annotation", "--ingress-controller-name", "example.com/mine"],
            new Dictionary<string, string?> { ["POD_NAMESPACE"] = "gw" });

        Assert.AreEqual("example.com/mine", options.IngressControllerName);
        Assert.IsTrue(options.WatchLegacyAnnotation);
        Assert.IsTrue(options.ToControllerOptions().Translator.WatchLegacyAnnotation);
    }

    [TestMethod]
    public void IngressOptions_AreReadFromTheEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["POD_NAMESPACE"] = "gw",
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium",
            ["TITANIUM_GATEWAY_WATCH_LEGACY_ANNOTATION"] = "true",
            ["TITANIUM_GATEWAY_INGRESS_CONTROLLER_NAME"] = "example.com/mine",
        };

        var options = GatewayOptions.Parse([], environment);

        Assert.IsTrue(options.WatchLegacyAnnotation);
        Assert.AreEqual("example.com/mine", options.IngressControllerName);
        Assert.AreEqual(0, options.IgnoredEnvironment.Count);
    }

    [TestMethod]
    public void Options_RequireTheDataPlaneService()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse(["--data-plane-namespace", "x", "--no-leader-election"], NoEnvironment));
    }

    [TestMethod]
    [DataRow("80")]
    [DataRow("80=x")]
    [DataRow("0=80")]
    [DataRow("80=70000")]
    public void PortMap_RejectsMalformedEntries(string text)
    {
        Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.ParsePortMap(text));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (host == "10.1.0.9")
            {
                throw new HttpRequestException("connection refused");
            }

            if (path == "/readyz")
            {
                return Task.FromResult(new HttpResponseMessage(host == "10.1.0.2" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            }

            var authorized = request.Headers.Authorization?.ToString() == "Bearer secret";
            return Task.FromResult(authorized
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"1\",\"config_hash\":\"abc123\"}") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    [TestMethod]
    public async Task Observer_ReportsTheHashOfEveryReadyPodAndNullForTheRest()
    {
        var handler = new StubHandler();
        var observer = new HttpDataPlaneObserver(new HttpClient(handler), 9901, "secret", NullLogger.Instance);

        var observation = await observer.ObserveAsync(["10.1.0.1", "10.1.0.2", "10.1.0.9"], CancellationToken.None);

        CollectionAssert.AreEqual(new string?[] { "abc123", null, null }, observation.PodHashes.ToArray());
        Assert.IsFalse(observation.Confirms("abc123"));
        Assert.IsTrue(handler.Requests.All(r => r.RequestUri!.Port == 9901));
    }

    [TestMethod]
    public async Task Observer_WithoutTheTokenCannotReadTheVersion()
    {
        var observer = new HttpDataPlaneObserver(new HttpClient(new StubHandler()), 9901, null, NullLogger.Instance);

        var observation = await observer.ObserveAsync(["10.1.0.1"], CancellationToken.None);

        Assert.IsNull(observation.PodHashes[0]);
    }

    [TestMethod]
    public async Task Observer_ConfirmsWhenEveryPodRunsTheHash()
    {
        var observer = new HttpDataPlaneObserver(new HttpClient(new StubHandler()), 9901, "secret", NullLogger.Instance);

        var observation = await observer.ObserveAsync(["10.1.0.1", "10.1.0.3"], CancellationToken.None);

        Assert.IsTrue(observation.Confirms("abc123"));
        Assert.IsFalse((await observer.ObserveAsync([], CancellationToken.None)).Confirms("abc123"));
    }
}
