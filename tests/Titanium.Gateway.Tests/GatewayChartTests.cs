using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;
using YamlDotNet.RepresentationModel;

namespace Titanium.Gateway.Tests;

/// <summary>
///     The Helm chart, the controller image, and the controller's own options agree. Static checks always run; the
///     <c>helm</c> checks run when helm is installed (CI installs it).
/// </summary>
[TestClass]
public sealed class GatewayChartTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ChartDir = Path.Combine(RepoRoot, "deploy", "helm", "titanium-gateway");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([ChartDir, .. parts]));

    private static YamlMappingNode Values()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Read("values.yaml"));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode Path_(YamlNode node, params string[] path)
    {
        foreach (var name in path)
        {
            node = ((YamlMappingNode)node)[name];
        }

        return node;
    }

    [TestMethod]
    public void BootstrapConfig_IsWhatTheControllerRendersForAnEmptyCluster_AndValidates()
    {
        var bootstrap = ((YamlScalarNode)Path_(Values(), "dataPlane", "bootstrapConfig")).Value!;

        var rendered = TwpConfigLoader.ToJson(new GatewayTranslator().Render(new ClusterState()).Config);

        Assert.AreEqual(rendered, bootstrap, "update dataPlane.bootstrapConfig in values.yaml");
        var config = TwpConfigLoader.LoadYaml(bootstrap);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        Assert.AreEqual(0, config.Listeners.Count, "the pods start with no listeners; the controller adds them");
    }

    [TestMethod]
    public void DefaultPorts_MatchTheTranslatorsPortMap()
    {
        var ports = ((YamlSequenceNode)Path_(Values(), "dataPlane", "ports")).Children
            .Cast<YamlMappingNode>()
            .ToDictionary(p => int.Parse(((YamlScalarNode)p["servicePort"]).Value!), p => int.Parse(((YamlScalarNode)p["containerPort"]).Value!));

        CollectionAssert.AreEquivalent(new GatewayTranslatorOptions().ListenerPortMap.ToList(), ports.ToList());
    }

    [TestMethod]
    public void ChartEnvironment_OnlyUsesOptionsTheControllerKnows()
    {
        var template = Read("templates", "controller-deployment.yaml");
        var names = Regex.Matches(template, @"name: (TITANIUM_GATEWAY_[A-Z_]+)").Select(m => m.Groups[1].Value).ToList();
        Assert.IsTrue(names.Count >= 8);

        var environment = names.ToDictionary(n => n, _ => (string?)"x");
        environment["POD_NAMESPACE"] = "gw";
        environment["TITANIUM_GATEWAY_PORT_MAP"] = "80=8080";
        environment["TITANIUM_GATEWAY_ADMIN_PORT"] = "9901";
        environment["TITANIUM_GATEWAY_HEALTH_PORT"] = "8081";

        // An option the controller does not know is rejected, so a typo in the chart fails here and not in a pod.
        var options = GatewayOptions.Parse([], environment);
        Assert.AreEqual("x", options.DataPlaneService);
        Assert.AreEqual("x", options.DataPlaneDeployment);
    }

    [TestMethod]
    public void Crds_AreTheStandardChannelBundleWithoutExperimentalKinds()
    {
        var crds = Read("crds", "gateway-api-standard-v1.2.1.yaml");

        foreach (var kind in new[] { "gatewayclasses", "gateways", "httproutes", "referencegrants" })
        {
            StringAssert.Contains(crds, $"name: {kind}.gateway.networking.k8s.io");
        }

        Assert.AreEqual(5, Regex.Matches(crds, @"^kind: CustomResourceDefinition", RegexOptions.Multiline).Count);
        Assert.AreEqual(5, Regex.Matches(crds, @"gateway\.networking\.k8s\.io/channel: standard").Count);
        Assert.AreEqual(5, Regex.Matches(crds, @"gateway\.networking\.k8s\.io/bundle-version: v1\.2\.1").Count);
        foreach (var experimental in new[] { "tcproutes", "udproutes", "tlsroutes", "backendtlspolicies" })
        {
            Assert.IsFalse(crds.Contains(experimental + ".gateway", StringComparison.Ordinal), experimental);
        }

        StringAssert.Contains(Read("Chart.yaml"), "gateway.networking.k8s.io/bundle-version: v1.2.1");
    }

    [TestMethod]
    public void Rbac_IsLeastPrivilege()
    {
        var rbac = Read("templates", "rbac.yaml");

        Assert.IsFalse(rbac.Contains("\"*\"", StringComparison.Ordinal), "no wildcard resources, verbs, or groups");
        Assert.IsFalse(Regex.IsMatch(rbac, @"verbs:.*\b(delete|deletecollection|escalate|bind|impersonate)\b"), "no destructive verbs");

        // Writes to ConfigMaps, Secrets and the Deployment are limited to the objects the chart creates.
        foreach (var resource in new[] { "configmaps", "secrets", "deployments" })
        {
            var rule = Regex.Match(rbac, $@"resources: \[""{resource}""\]\s+resourceNames: \[[^\]]+\]\s+verbs: \[([^\]]+)\]");
            Assert.IsTrue(rule.Success, $"{resource} writes must be limited by resourceNames");
            StringAssert.DoesNotMatch(rule.Groups[1].Value, new Regex("create|list|watch"));
        }

        // Reading Secrets is the only cluster-wide Secret access, and it can be narrowed to listed namespaces.
        StringAssert.Contains(rbac, "if not .Values.controller.secretNamespaces");
        StringAssert.Contains(rbac, "range .Values.controller.secretNamespaces");
    }

    [TestMethod]
    public void Deployments_AreHardenedAndKeepTheDirectoryMount()
    {
        var controller = Read("templates", "controller-deployment.yaml");
        var dataPlane = Read("templates", "dataplane.yaml");
        var values = Read("values.yaml");

        Assert.IsFalse(dataPlane.Contains("subPath", StringComparison.Ordinal), "a subPath mount hides the ..data swap from --watch");
        StringAssert.Contains(dataPlane, "mountPath: /etc/titanium");
        StringAssert.Contains(dataPlane, "optional: true");
        StringAssert.Contains(dataPlane, "automountServiceAccountToken: false");
        StringAssert.Contains(controller, "path: /readyz");
        StringAssert.Contains(values, "replicaCount: 2");
        StringAssert.Contains(values, "readOnlyRootFilesystem: true");
        StringAssert.Contains(values, "runAsNonRoot: true");
        StringAssert.Contains(values, "allowPrivilegeEscalation: false");
        StringAssert.Contains(values, "ghcr.io/justcoding121/titanium-gateway");
        StringAssert.Contains(values, "ghcr.io/justcoding121/titanium-web-proxy");
    }

    [TestMethod]
    public void Rbac_GrantsTheIngressAccessAndNothingMore()
    {
        var rbac = Read("templates", "rbac.yaml");

        StringAssert.Matches(rbac, new Regex(@"apiGroups: \[""networking\.k8s\.io""\]\s+resources: \[""ingresses"", ""ingressclasses""\]\s+verbs: \[""get"", ""list"", ""watch""\]"));
        StringAssert.Matches(rbac, new Regex(@"apiGroups: \[""networking\.k8s\.io""\]\s+resources: \[""ingresses/status""\]\s+verbs: \[""get"", ""patch"", ""update""\]"));
        StringAssert.Matches(rbac, new Regex(@"apiGroups: \[""""\]\s+resources: \[""events""\]\s+verbs: \[""create"", ""patch""\]"));
        Assert.IsFalse(Regex.IsMatch(rbac, @"resources: \[""ingresses""[^\]]*\]\s+verbs: \[[^\]]*(update|patch|create)"), "the Ingress spec is read-only");
    }

    [TestMethod]
    public void IngressValues_AndTheIngressClass_AgreeWithTheController()
    {
        var values = Values();
        Assert.AreEqual(IngressApiNames.DefaultControllerName, ((YamlScalarNode)Path_(values, "controller", "ingressControllerName")).Value);
        Assert.AreEqual("false", ((YamlScalarNode)Path_(values, "controller", "watchLegacyAnnotation")).Value);
        Assert.AreEqual(IngressApiNames.DefaultClassName, ((YamlScalarNode)Path_(values, "ingressClass", "name")).Value);
        Assert.AreEqual(new GatewayTranslatorOptions().IngressControllerName, IngressApiNames.DefaultControllerName);

        var template = Read("templates", "ingressclass.yaml");
        StringAssert.Contains(template, "kind: IngressClass");
        StringAssert.Contains(template, "controller: {{ .Values.controller.ingressControllerName }}");
        StringAssert.Contains(template, IngressApiNames.DefaultClassAnnotation);

        var deployment = Read("templates", "controller-deployment.yaml");
        StringAssert.Contains(deployment, "TITANIUM_GATEWAY_INGRESS_CONTROLLER_NAME");
        StringAssert.Contains(deployment, "if .Values.controller.watchLegacyAnnotation");
    }

    [TestMethod]
    public void IngressEnvironment_FlowsIntoTheTranslatorOptions()
    {
        var environment = new Dictionary<string, string?>
        {
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium-data",
            ["POD_NAMESPACE"] = "gw",
            ["TITANIUM_GATEWAY_INGRESS_CONTROLLER_NAME"] = "example.com/mine",
            ["TITANIUM_GATEWAY_WATCH_LEGACY_ANNOTATION"] = "true",
        };

        var translator = GatewayOptions.Parse([], environment).ToControllerOptions().Translator;

        Assert.AreEqual("example.com/mine", translator.IngressControllerName);
        Assert.IsTrue(translator.WatchLegacyAnnotation);
    }

    [TestMethod]
    public void Dockerfile_KeepsItsHardeningAndContract()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "packaging", "docker", "Dockerfile.gateway"));

        StringAssert.Contains(text, "USER 1654:1654");
        StringAssert.Contains(text, "ENTRYPOINT [\"/app/titanium-gateway\"]");
        StringAssert.Contains(text, "amd64) RID=linux-x64");
        StringAssert.Contains(text, "arm64) RID=linux-arm64");
        StringAssert.Contains(text, "src/Titanium.Gateway/Titanium.Gateway.csproj");
        Assert.IsFalse(text.Contains("USER root", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Helm_LintsAndRenders_WithAndWithoutSecretNamespaces()
    {
        var helm = FindHelm();
        if (helm is null)
        {
            Assert.Inconclusive("helm is not installed.");
            return;
        }

        var (lintExit, lintOut) = Run(helm, $"lint \"{ChartDir}\" --strict");
        Assert.AreEqual(0, lintExit, lintOut);

        var (exit, rendered) = Run(helm, $"template rel \"{ChartDir}\" --namespace gw");
        Assert.AreEqual(0, exit, rendered);
        StringAssert.Contains(rendered, "kind: GatewayClass");
        StringAssert.Contains(rendered, "name: rel-titanium-gateway-data");
        StringAssert.Contains(rendered, "TITANIUM_GATEWAY_PORT_MAP");
        Assert.IsFalse(rendered.Contains("kind: Role\nmetadata:\n  name: rel-titanium-gateway-controller-secrets", StringComparison.Ordinal));

        var (scopedExit, scoped) = Run(helm, $"template rel \"{ChartDir}\" --namespace gw --set controller.secretNamespaces={{a,b}}");
        Assert.AreEqual(0, scopedExit, scoped);
        Assert.AreEqual(2, Regex.Matches(scoped, @"kind: Role\r?\nmetadata:\r?\n  name: rel-titanium-gateway-controller-secrets\r?\n  namespace: (a|b)").Count);
        StringAssert.Contains(scoped, "TITANIUM_GATEWAY_SECRET_NAMESPACES");
        Assert.IsFalse(Regex.IsMatch(scoped, @"resources: \[""secrets""\]\s+verbs: \[""get"", ""list"", ""watch""\]\s+---\s+apiVersion: rbac.authorization.k8s.io/v1\s+kind: ClusterRoleBinding"),
            "with secretNamespaces the ClusterRole carries no Secret rule");

        var (graceExit, grace) = Run(helm, $"template rel \"{ChartDir}\" --set dataPlane.terminationGracePeriodSeconds=3");
        Assert.AreNotEqual(0, graceExit);
        StringAssert.Contains(grace, "terminationGracePeriodSeconds");
    }

    private static string? FindHelm()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HELM_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "helm.exe" } : new[] { "helm" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static (int Exit, string Output) Run(string file, string arguments)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(60_000), "helm timed out");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "helm", "titanium-gateway", "Chart.yaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (deploy/helm/titanium-gateway) not found.");
    }
}
