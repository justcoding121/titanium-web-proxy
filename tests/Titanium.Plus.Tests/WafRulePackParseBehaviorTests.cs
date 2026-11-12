using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Waf;

namespace Titanium.Plus.Tests;

/// <summary>
///     Fault and golden paths for <see cref="WafRulePack.Parse"/>.
///     Bug classes: bad pack JSON loads as empty, wrong pack id is accepted, duplicate ids collide silently.
/// </summary>
[TestClass]
public class WafRulePackParseBehaviorTests
{
    [TestMethod]
    public void Parse_RejectsInvalidJson_EmptyRules_AndWrongPackId()
    {
        var badJson = Assert.ThrowsExactly<WafRuleException>(() => WafRulePack.Parse("{not-json"));
        StringAssert.Contains(badJson.Message, "not valid JSON");

        var empty = Assert.ThrowsExactly<WafRuleException>(() => WafRulePack.Parse("""{"pack":"waf-pack-1","rules":[]}"""));
        StringAssert.Contains(empty.Message, "no rules");

        var wrongPack = Assert.ThrowsExactly<WafRuleException>(() => WafRulePack.Parse("""
            {"pack":"waf-pack-0","rules":[{"id":"TW-TEST-001","category":"sqli","severity":"error","targets":["path"],"pattern":"a"}]}
            """));
        StringAssert.Contains(wrongPack.Message, "expected 'waf-pack-1'");
    }

    [TestMethod]
    public void Parse_RejectsDuplicateIds_AndAcceptsMinimalValidRule()
    {
        var dup = Assert.ThrowsExactly<WafRuleException>(() => WafRulePack.Parse("""
            {"pack":"waf-pack-1","rules":[
              {"id":"TW-TEST-001","category":"sqli","severity":"error","targets":["path"],"pattern":"abc"},
              {"id":"tw-test-001","category":"sqli","severity":"error","targets":["path"],"pattern":"def"}
            ]}
            """));
        StringAssert.Contains(dup.Message, "twice");

        var rules = WafRulePack.Parse("""
            {"pack":"waf-pack-1","rules":[
              {"id":"TW-TEST-002","category":"sqli","severity":"error","targets":["path","query"],"pattern":"\\\\bselect\\\\b","description":"test"}
            ]}
            """);
        Assert.AreEqual(1, rules.Count);
        Assert.AreEqual("TW-TEST-002", rules[0].Id);
    }
}
