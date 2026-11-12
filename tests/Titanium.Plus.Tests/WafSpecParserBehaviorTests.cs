using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Waf;

namespace Titanium.Plus.Tests;

/// <summary>
///     WafSpecParser severity/check/target fault strings.
/// </summary>
[TestClass]
public class WafSpecParserBehaviorTests
{
    private static readonly string[] ExpectedUserAgentHeaders = ["User-Agent"];

    [TestMethod]
    public void ParseSeverity_AndCheck_RejectUnknown_AndAcceptKnown()
    {
        Assert.AreEqual(WafSeverity.Critical, WafSpecParser.ParseSeverity("r1", "critical"));
        var badSev = Assert.ThrowsExactly<WafRuleException>(() => WafSpecParser.ParseSeverity("r1", "loud"));
        StringAssert.Contains(badSev.Message, "severity");

        Assert.AreEqual(WafCheck.None, WafSpecParser.ParseCheck("r1", null));
        Assert.AreEqual(WafCheck.None, WafSpecParser.ParseCheck("r1", "  "));
        Assert.AreEqual(WafCheck.NullByte, WafSpecParser.ParseCheck("r1", "nullByte"));
        var badCheck = Assert.ThrowsExactly<WafRuleException>(() => WafSpecParser.ParseCheck("r1", "explode"));
        StringAssert.Contains(badCheck.Message, "known check");
        var noneLiteral = Assert.ThrowsExactly<WafRuleException>(() => WafSpecParser.ParseCheck("r1", "none"));
        StringAssert.Contains(noneLiteral.Message, "known check");
    }

    [TestMethod]
    public void ParseTargets_AcceptsKnown_AndRejectsBadHeaderForms()
    {
        var targets = WafSpecParser.ParseTargets("r1", ["path", "query", "header:User-Agent", "cookie", "body"], null, out var headers);
        Assert.IsTrue(targets.HasFlag(WafTargets.Path));
        Assert.IsTrue(targets.HasFlag(WafTargets.Query));
        Assert.IsTrue(targets.HasFlag(WafTargets.Header));
        Assert.IsTrue(targets.HasFlag(WafTargets.Cookie));
        Assert.IsTrue(targets.HasFlag(WafTargets.Body));
        CollectionAssert.AreEqual(ExpectedUserAgentHeaders, headers!.ToArray());

        var badHeader = Assert.ThrowsExactly<WafRuleException>(() =>
            WafSpecParser.ParseTargets("r1", ["cookie:Name"], null, out _));
        StringAssert.Contains(badHeader.Message, "header:<Name>");

        var trailing = Assert.ThrowsExactly<WafRuleException>(() =>
            WafSpecParser.ParseTargets("r1", ["header:"], null, out _));
        StringAssert.Contains(trailing.Message, "header:<Name>");

        var unknown = Assert.ThrowsExactly<WafRuleException>(() =>
            WafSpecParser.ParseTargets("r1", ["trailer"], null, out _));
        StringAssert.Contains(unknown.Message, "path, query, header");
    }
}
