using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class EscapeHatchConfigTests
{
    [TestMethod]
    public void UnknownKey_SuggestsDidYouMean()
    {
        const string yaml = """
            server:
              limtis:
                maxHeaderCount: 10
            """;

        var warnings = UnknownConfigKeyScanner.Scan(yaml, yaml: true);

        Assert.IsTrue(warnings.Count > 0);
        StringAssert.Contains(warnings[0], "limtis");
        StringAssert.Contains(warnings[0], "limits");
    }

    [TestMethod]
    public void HatchCeiling_RejectsAboveAndAcceptsAtCeiling()
    {
        var tooHigh = TwpConfigValidator.Validate(new TwpConfig
        {
            Server = new ServerConfig
            {
                Limits = new LimitsConfig { MaxHttp3FramePayloadBytes = (64L * 1024 * 1024) + 1 },
            },
        });
        Assert.IsTrue(tooHigh.Any(e => e.Contains("maxHttp3FramePayloadBytes", StringComparison.Ordinal)));

        var atCeiling = TwpConfigValidator.Validate(new TwpConfig
        {
            Server = new ServerConfig
            {
                Limits = new LimitsConfig { MaxHttp3FramePayloadBytes = 64L * 1024 * 1024 },
            },
        });
        Assert.IsFalse(atCeiling.Any(e => e.Contains("maxHttp3FramePayloadBytes", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ReservedBodyKeys_WarnWithoutFailingValidation()
    {
        var config = new TwpConfig
        {
            Server = new ServerConfig
            {
                Limits = new LimitsConfig { MaxDecompressionRatio = 50, MaxEncodedBodyBytes = 10 },
            },
        };

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        var warnings = TwpConfigValidator.CollectWarnings(config);
        Assert.IsTrue(warnings.Any(w => w.Contains("maxDecompressionRatio", StringComparison.Ordinal)));
        Assert.IsTrue(warnings.Any(w => w.Contains("maxEncodedBodyBytes", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SchemaFile_MatchesTheModel()
    {
        var generated = TwpConfigSchema.Generate().Replace("\r\n", "\n");
        var path = FindRepoFile(Path.Combine("src", "Titanium.Web.Proxy.Configuration", "twp.schema.json"));
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_TWP_SCHEMA"), "1", StringComparison.Ordinal))
            File.WriteAllText(path, generated);

        var committed = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.AreEqual(generated, committed);
    }

    private static string FindRepoFile(string relativeUnderRepo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeUnderRepo);
            if (File.Exists(candidate) ||
                string.Equals(Environment.GetEnvironmentVariable("UPDATE_TWP_SCHEMA"), "1", StringComparison.Ordinal) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                if (candidate.Contains("twp.schema.json", StringComparison.Ordinal) &&
                    Directory.Exists(Path.GetDirectoryName(candidate)))
                    return candidate;
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail($"Could not locate {relativeUnderRepo}");
        return null!;
    }
}
