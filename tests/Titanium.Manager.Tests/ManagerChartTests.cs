using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YamlDotNet.RepresentationModel;

namespace Titanium.Manager.Tests;

/// <summary>
///     The Helm chart and the manager image agree: single replica, durable PVC, probes on <c>/v1/healthz</c>, and
///     hardened defaults. Static checks always run; <c>helm</c> checks run when helm is installed (CI installs it).
/// </summary>
[TestClass]
public sealed class ManagerChartTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ChartDir = Path.Combine(RepoRoot, "deploy", "helm", "titanium-manager");

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
    public void Dockerfile_KeepsItsHardeningAndContract()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "tools", "packaging", "docker", "Dockerfile.manager"));

        StringAssert.Contains(text, "USER 1654:1654");
        StringAssert.Contains(text, "ENTRYPOINT [\"titanium-manager\", \"--listen\", \"+:7700/\", \"--db\", \"/var/lib/titanium-manager/manager.db\"]");
        StringAssert.Contains(text, "titanium-manager\", \"healthcheck\"");
        StringAssert.Contains(text, "amd64) RID=linux-x64");
        StringAssert.Contains(text, "arm64) RID=linux-arm64");
        StringAssert.Contains(text, "src/Titanium.Manager/Titanium.Manager.csproj");
        Assert.IsFalse(text.Contains("USER root", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Deployment_ProbesPvcAndSingleReplicaContract()
    {
        var deployment = Read("templates", "deployment.yaml");
        var pvc = Read("templates", "pvc.yaml");
        var values = Read("values.yaml");

        StringAssert.Contains(deployment, "path: /v1/healthz");
        StringAssert.Contains(deployment, "replicaCount to 1");
        StringAssert.Contains(deployment, "persistence.enabled must be true");
        StringAssert.Contains(deployment, "TITANIUM_MANAGER_ADMIN_TOKEN");
        StringAssert.Contains(deployment, "mountPath: {{ .Values.persistence.mountPath }}");
        StringAssert.Contains(pvc, "kind: PersistentVolumeClaim");
        StringAssert.Contains(values, "replicaCount: 1");
        StringAssert.Contains(values, "mountPath: /var/lib/titanium-manager");
        StringAssert.Contains(values, "readOnlyRootFilesystem: true");
        StringAssert.Contains(values, "runAsNonRoot: true");
        StringAssert.Contains(values, "automount: false");
        StringAssert.Contains(values, "ghcr.io/justcoding121/titanium-manager");
    }

    [TestMethod]
    public void CiSmokeValues_UseTheLocalImageTag()
    {
        var smoke = Read("ci", "smoke-values.yaml");
        StringAssert.Contains(smoke, "repository: titanium-manager");
        StringAssert.Contains(smoke, "tag: ci");
        StringAssert.Contains(smoke, "pullPolicy: Never");
    }

    [TestMethod]
    public void Helm_LintsAndRenders_AndRejectsMultipleReplicas()
    {
        var helm = FindHelm();
        if (helm is null)
        {
            Assert.Inconclusive("helm is not installed.");
            return;
        }

        foreach (var values in new string?[] { null, "ci/smoke-values.yaml" })
        {
            var valuesArgs = values is null
                ? string.Empty
                : $" -f \"{Path.Combine(ChartDir, values)}\"";
            var (lintExit, lintOut) = Run(helm, $"lint \"{ChartDir}\" --strict{valuesArgs}");
            Assert.AreEqual(0, lintExit, lintOut);

            var (templateExit, rendered) = Run(helm, $"template rel \"{ChartDir}\"{valuesArgs}");
            Assert.AreEqual(0, templateExit, rendered);
            StringAssert.Contains(rendered, "path: /v1/healthz");
            StringAssert.Contains(rendered, "kind: PersistentVolumeClaim");
        }

        var (multiExit, multiOut) = Run(helm, $"template rel \"{ChartDir}\" --set replicaCount=2");
        Assert.AreNotEqual(0, multiExit);
        StringAssert.Contains(multiOut, "single-instance only");
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
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "helm", "titanium-manager", "Chart.yaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (deploy/helm/titanium-manager) not found.");
    }
}
