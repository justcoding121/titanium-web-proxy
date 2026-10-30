using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YamlDotNet.RepresentationModel;

namespace Titanium.Gateway.Tests;

/// <summary>The conformance scaffolding cannot run here (it needs Linux, Docker, and kind), so these tests keep it consistent.</summary>
[TestClass]
public partial class GatewayConformanceScaffoldingTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string[] SupportedFeaturesCap =
    [
        "HTTPRouteQueryParamMatching", "HTTPRouteMethodMatching", "HTTPRouteHostRewrite", "HTTPRoutePathRewrite", "HTTPRouteRequestMirror",
    ];

    [GeneratedRegex(@"v\d+\.\d+\.\d+")]
    private static partial Regex GatewayApiCrdVersionRegex();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    private static YamlMappingNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    [TestMethod]
    public void Workflow_IsValidYaml_AndRunsTheScriptOnLinux()
    {
        var text = Read(".github", "workflows", "gateway-conformance.yml");
        var root = Load(text);
        var job = (YamlMappingNode)((YamlMappingNode)root["jobs"])["conformance"];
        Assert.AreEqual("ubuntu-latest", ((YamlScalarNode)job["runs-on"]).Value);
        StringAssert.Contains(text, "tools/gateway-conformance/run.sh");
        StringAssert.Contains(text, "upload-artifact", "the report is kept");
        StringAssert.Contains(text, "continue-on-error: true", "the first runs report only");
        StringAssert.Contains(text, "GATEWAY_CONFORMANCE_ENFORCE");
    }

    [TestMethod]
    public void Script_RunsTheHttpProfileForTheTitaniumClass_AndClaimsOnlyWhatIsImplemented()
    {
        var script = Read("tools", "gateway-conformance", "run.sh");
        StringAssert.Contains(script, "--conformance-profiles=GATEWAY-HTTP");
        StringAssert.Contains(script, "--gateway-class=titanium");
        StringAssert.Contains(script, "--report-output=");
        var line = script.Split('\n').Single(l => l.StartsWith("SUPPORTED_FEATURES=", StringComparison.Ordinal));
        // run.sh is checked out with CRLF on Windows CI; TrimEntries strips the trailing '\r'.
        var features = line["SUPPORTED_FEATURES=".Length..]
            .Trim()
            .Trim('"')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CollectionAssert.IsSubsetOf(features, SupportedFeaturesCap);
        foreach (var unsupported in new[]
        {
            "HTTPRouteBackendTimeout", "HTTPRouteRequestTimeout", "HTTPRouteBackendProtocolH2C", "GatewayPort8080", "HTTPRouteParentRefPort",
            "HTTPRouteResponseHeaderModification", "HTTPRouteSchemeRedirect", "HTTPRoutePortRedirect", "HTTPRoutePathRedirect",
        })
        {
            Assert.IsFalse(features.Contains(unsupported), unsupported + " is not implemented and must not be claimed.");
        }
    }

    [TestMethod]
    public void GoModule_PinsTheGatewayApiReleaseTheChartCrdsComeFrom()
    {
        var goMod = Read("tests", "gateway-conformance", "go.mod");
        var crd = Directory.GetFiles(Path.Combine(RepoRoot, "deploy", "helm", "titanium-gateway", "crds")).Single();
        var version = GatewayApiCrdVersionRegex().Match(Path.GetFileName(crd)).Value;
        StringAssert.Contains(goMod, "sigs.k8s.io/gateway-api " + version);
        StringAssert.Contains(Read("tests", "gateway-conformance", "conformance_test.go"), "conformance.RunConformance(t)");
    }

    [TestMethod]
    public void KindValues_GiveTheDataPlaneALoadBalancerAddress()
    {
        var values = Load(Read("deploy", "helm", "titanium-gateway", "ci", "kind-values.yaml"));
        var service = (YamlMappingNode)((YamlMappingNode)values["dataPlane"])["service"];
        Assert.AreEqual("LoadBalancer", ((YamlScalarNode)service["type"]).Value);
        Assert.AreEqual("Never", ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)values["controller"])["image"])["pullPolicy"]).Value);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "gateway-conformance", "run.sh")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (tools/gateway-conformance) not found.");
    }
}
