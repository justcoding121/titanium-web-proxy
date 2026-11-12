using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Waf;

namespace Titanium.Plus.Tests;

/// <summary>
///     WafCustomRules.LoadFile IO wrappers and absolute-form ScanTarget normalization.
/// </summary>
[TestClass]
public class WafCustomRulesAndScannerBehaviorTests
{
    [TestMethod]
    public void LoadFile_ReadsYaml_AndWrapsMissingPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "twp-waf-missing-" + Guid.NewGuid().ToString("N") + ".yaml");
        var ex = Assert.ThrowsExactly<WafRuleException>(() => WafCustomRules.LoadFile(missing));
        StringAssert.Contains(ex.Message, "cannot be read");

        var path = Path.Combine(Path.GetTempPath(), "twp-waf-rules-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            rules:
              - id: ACME-LOAD-1
                targets: [query]
                regex: 'a{1,20}b'
            """);
        try
        {
            var rules = WafCustomRules.LoadFile(path);
            Assert.AreEqual(1, rules.Count);
            Assert.AreEqual("ACME-LOAD-1", rules[0].Id);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void Scanner_AbsoluteFormTarget_StillMatchesPathRules()
    {
        // Bug class: absolute-form targets skip path rules because the scheme/host stays in the scan text.
        var engine = new WafEngine(new WafOptions { Mode = WafMode.Block, ParanoiaLevel = 1 });
        var clean = engine.Inspect(
            new WafRequest { Target = "http://origin.example/ok#frag" },
            engine.DefaultProfile);
        Assert.IsFalse(clean.Blocked);

        var hit = engine.Inspect(
            new WafRequest { Target = "https://origin.example/q?x=1%20union%20select%201" },
            engine.DefaultProfile);
        Assert.IsTrue(hit.WouldBlock || hit.Score > 0, "absolute-form query must still be screened");
    }
}
