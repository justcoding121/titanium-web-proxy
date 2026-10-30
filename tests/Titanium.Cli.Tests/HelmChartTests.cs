using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Web.Proxy.Configuration;
using YamlDotNet.RepresentationModel;

namespace Titanium.Cli.Tests;

/// <summary>
///     The container image and Helm chart agree with the CLI: the shipped default configs pass the validator, the
///     Dockerfile keeps its hardening directives, and (when <c>helm</c> is installed) the chart lints and renders.
/// </summary>
[TestClass]
public class HelmChartTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ChartDir = Path.Combine(RepoRoot, "deploy", "helm", "titanium");

    [TestMethod]
    public void ValuesYaml_DefaultConfig_PassesTheValidator()
    {
        AssertConfigIsValid(ConfigFromValues(Path.Combine(ChartDir, "values.yaml")));
    }

    [TestMethod]
    public void CiValues_FullConfig_PassesTheValidator()
    {
        AssertConfigIsValid(ConfigFromValues(Path.Combine(ChartDir, "ci", "full-values.yaml")));
        AssertConfigIsValid(ConfigFromValues(Path.Combine(ChartDir, "ci", "smoke-values.yaml")));
    }

    [TestMethod]
    public void Dockerfile_KeepsItsHardeningAndContract()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "packaging", "docker", "Dockerfile"));

        StringAssert.Contains(text, "STOPSIGNAL SIGTERM");
        StringAssert.Contains(text, "HEALTHCHECK");
        StringAssert.Contains(text, "\"titanium\", \"healthcheck\"");
        StringAssert.Contains(text, "ENTRYPOINT [\"titanium\", \"run\", \"--config\", \"/etc/titanium/twp.yaml\"]");
        StringAssert.Contains(text, "USER 1654:1654");
        StringAssert.Contains(text, "amd64) RID=linux-x64");
        StringAssert.Contains(text, "arm64) RID=linux-arm64");
        Assert.IsFalse(text.Contains("USER root", StringComparison.Ordinal), "the runtime image must not run as root");
        Assert.IsFalse(
            text.Split('\n').Any(line => line.TrimStart().StartsWith("ARG ", StringComparison.Ordinal) &&
                                         line.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)),
            "no secrets as build args");
    }

    [TestMethod]
    public void DeploymentTemplate_UsesTheAdminProbesAndAHardenedPod()
    {
        var deployment = File.ReadAllText(Path.Combine(ChartDir, "templates", "deployment.yaml"));
        var values = File.ReadAllText(Path.Combine(ChartDir, "values.yaml"));

        StringAssert.Contains(deployment, "path: /healthz");
        StringAssert.Contains(deployment, "path: /readyz");
        StringAssert.Contains(deployment, "terminationGracePeriodSeconds");
        StringAssert.Contains(deployment, "mountPath: /etc/titanium");
        Assert.IsFalse(deployment.Contains("subPath", StringComparison.Ordinal),
            "a subPath mount hides the ..data symlink swap from --watch");
        StringAssert.Contains(values, "readOnlyRootFilesystem: true");
        StringAssert.Contains(values, "runAsNonRoot: true");
        StringAssert.Contains(values, "allowPrivilegeEscalation: false");
    }

    [TestMethod]
    [DataRow("release.yml")]
    [DataRow("kubernetes-ci.yml")]
    [DataRow("kubernetes-smoke.yml")]
    public void Workflows_AreValidYaml(string name)
    {
        using var reader = new StreamReader(Path.Combine(RepoRoot, ".github", "workflows", name));
        var stream = new YamlStream();
        stream.Load(reader);
        Assert.AreEqual(1, stream.Documents.Count);
        Assert.IsInstanceOfType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    [TestMethod]
    public void ReleaseWorkflow_PublishesASignedMultiArchImageToGhcr()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));

        StringAssert.Contains(text, "ghcr.io/justcoding121/titanium-web-proxy");
        StringAssert.Contains(text, "platforms: linux/amd64,linux/arm64");
        StringAssert.Contains(text, "sbom: true");
        StringAssert.Contains(text, "cosign sign --yes");

        var ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "kubernetes-ci.yml"));
        StringAssert.Contains(ci, "push: false");
        Assert.IsFalse(ci.Contains("push: true", StringComparison.Ordinal), "pull-request builds must not push");
    }

    [TestMethod]
    public void Helm_Lints_AndRendersConfigThatPassesTheValidator()
    {
        var helm = FindHelm();
        if (helm is null)
        {
            Assert.Inconclusive("helm is not installed.");
            return;
        }

        foreach (var values in new[] { null, "ci/full-values.yaml", "ci/smoke-values.yaml" })
        {
            var valuesArgs = values is null ? string.Empty : $" -f \"{Path.Combine(ChartDir, values)}\"";
            var (lintExit, lintOut) = Run(helm, $"lint \"{ChartDir}\"{valuesArgs}");
            Assert.AreEqual(0, lintExit, lintOut);

            var (templateExit, rendered) = Run(helm, $"template rel \"{ChartDir}\"{valuesArgs}");
            Assert.AreEqual(0, templateExit, rendered);

            var yaml = ConfigMapTwpYaml(rendered);
            var config = TwpConfigLoader.LoadYaml(yaml);
            AssertConfigIsValid(config);

            var admin = config.Server?.Admin;
            Assert.IsNotNull(admin);
            Assert.IsTrue(admin.Enabled);
            Assert.AreEqual("0.0.0.0", admin.Host);
            Assert.AreEqual("TITANIUM_ADMIN_TOKEN", admin.AuthTokenEnv);
            Assert.AreEqual("http://127.0.0.1:9901/healthz", HealthcheckCommand.ResolveUrl(config, out _));
            StringAssert.Contains(rendered, "TITANIUM_ADMIN_TOKEN");
            StringAssert.Contains(rendered, "path: /readyz");
        }
    }

    [TestMethod]
    public void Helm_RejectsAGracePeriodShorterThanThePreStopDelay_AndPlusWithoutASecret()
    {
        var helm = FindHelm();
        if (helm is null)
        {
            Assert.Inconclusive("helm is not installed.");
            return;
        }

        var (graceExit, graceOut) = Run(helm,
            $"template rel \"{ChartDir}\" --set terminationGracePeriodSeconds=5 --set admin.preStopDelaySeconds=10");
        Assert.AreNotEqual(0, graceExit);
        StringAssert.Contains(graceOut, "terminationGracePeriodSeconds must be greater");

        var (plusExit, plusOut) = Run(helm, $"template rel \"{ChartDir}\" --set plus.enabled=true");
        Assert.AreNotEqual(0, plusExit);
        StringAssert.Contains(plusOut, "plus.controlPlane.sharedSecret.existingSecret is required");
    }

    private static void AssertConfigIsValid(Titanium.Web.Proxy.Configuration.Models.TwpConfig config)
    {
        var errors = TwpConfigValidator.Validate(config);
        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
    }

    /// <summary>The <c>config:</c> block of a values file, loaded the way the CLI loads twp.yaml.</summary>
    private static Titanium.Web.Proxy.Configuration.Models.TwpConfig ConfigFromValues(string valuesPath)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(valuesPath))
        {
            stream.Load(reader);
        }

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var defaults = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(ChartDir, "values.yaml")))
        {
            defaults.Load(reader);
        }

        var node = root.Children.TryGetValue(new YamlScalarNode("config"), out var own)
            ? own
            : ((YamlMappingNode)defaults.Documents[0].RootNode).Children[new YamlScalarNode("config")];

        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(node)).Save(writer, assignAnchors: false);
        var config = TwpConfigLoader.LoadYaml(writer.ToString());

        // The chart adds server.admin; mirror just enough of it that a non-loopback admin is not the failure.
        config.Server ??= new Titanium.Web.Proxy.Configuration.Models.ServerConfig();
        config.Server.Admin = new Titanium.Web.Proxy.Configuration.Models.AdminConfig
        {
            Enabled = true,
            Host = "0.0.0.0",
            Port = 9901,
            AuthTokenEnv = "TITANIUM_ADMIN_TOKEN",
        };
        return config;
    }

    private static string ConfigMapTwpYaml(string renderedManifests)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(renderedManifests));
        foreach (var document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode map ||
                !map.Children.TryGetValue(new YamlScalarNode("kind"), out var kind) ||
                ((YamlScalarNode)kind).Value != "ConfigMap")
            {
                continue;
            }

            var data = (YamlMappingNode)map.Children[new YamlScalarNode("data")];
            return ((YamlScalarNode)data.Children[new YamlScalarNode("twp.yaml")]).Value!;
        }

        throw new AssertFailedException("no ConfigMap in the rendered chart");
    }

    private static string? FindHelm()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HELM_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "helm.exe" } : new[] { "helm" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(60_000), "helm timed out");
        return (process.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "helm", "titanium", "Chart.yaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (deploy/helm/titanium) not found.");
    }
}
