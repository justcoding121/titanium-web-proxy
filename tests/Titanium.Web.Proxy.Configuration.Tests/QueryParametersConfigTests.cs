using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class QueryParametersConfigTests
{
    private const string Header = """
        schemaVersion: "7.1"
        listeners:
          - port: 8080
        clusters:
          - id: c
            destinations:
              - id: d
                address: 127.0.0.1
                port: 9000
        routes:
          - id: r
            clusterId: c
            match:
              path: /a
              queryParameters:

        """;

    [TestMethod]
    public void Yaml_BindsQueryParametersAndValidates()
    {
        var yaml = Header + """
                  - name: env
                    value: lab
                  - name: v
                    value: "[0-9]+"
                    kind: Regex
            """;
        var config = TwpConfigLoader.LoadYaml(yaml);

        var entries = config.Routes[0].Match.QueryParameters;
        Assert.IsNotNull(entries);
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(QueryMatchKind.Exact, entries[0].Kind);
        Assert.AreEqual(QueryMatchKind.Regex, entries[1].Kind);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void Validator_RejectsABrokenRegex()
    {
        var yaml = Header + """
                  - name: v
                    value: "("
                    kind: Regex
            """;
        var errors = TwpConfigValidator.Validate(TwpConfigLoader.LoadYaml(yaml));

        Assert.IsTrue(errors.Any(e => e.Contains("queryParameters 'v'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OmittedQueryParameters_StayNull()
    {
        var yaml = Header.Replace("      queryParameters:\n", string.Empty, StringComparison.Ordinal)
            .Replace("      queryParameters:\r\n", string.Empty, StringComparison.Ordinal);
        var config = TwpConfigLoader.LoadYaml(yaml);

        Assert.IsNull(config.Routes[0].Match.QueryParameters);
    }
}
