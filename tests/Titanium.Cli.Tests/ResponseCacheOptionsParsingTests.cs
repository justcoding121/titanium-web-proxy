using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Caching;

namespace Titanium.Cli.Tests;

[TestClass]
public class ResponseCacheOptionsParsingTests
{
    private static Dictionary<string, string> Opts(params (string Key, string Value)[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            map[key] = value;
        }

        return map;
    }

    [TestMethod]
    public void Defaults_Are64MiB_1MiB_AndConservative()
    {
        var warnings = new List<string>();
        var parsed = RunCommand.ParseResponseCacheOptions(Opts(("cache.enable", "true")), warnings.Add);

        Assert.AreEqual(64L * 1024 * 1024, parsed.MaxBytes);
        Assert.AreEqual(1024 * 1024, parsed.Entry.MaxEntryBytes);
        Assert.IsFalse(parsed.Entry.ExposeHeaders, "X-Cache and Age are opt-in");
        Assert.IsFalse(parsed.Entry.HeuristicFreshness, "heuristic freshness is off by default");
        Assert.IsNull(parsed.Entry.DefaultTtl, "no implicit ttl: only responses that declare freshness are stored");
        Assert.IsTrue(parsed.Entry.Collapse);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void Keys_AreApplied()
    {
        var warnings = new List<string>();
        var parsed = RunCommand.ParseResponseCacheOptions(Opts(
            ("cache.maxBytes", "1048576"),
            ("cache.maxEntryBytes", "65536"),
            ("cache.exposeHeaders", "true"),
            ("cache.heuristicFreshness", "yes"),
            ("cache.defaultTtlSeconds", "30"),
            ("cache.collapse", "false"),
            ("cache.collapse.maxWaiters", "8"),
            ("cache.collapse.timeoutMs", "250"),
            ("cache.staleRetentionSeconds", "10")), warnings.Add);

        Assert.AreEqual(1048576L, parsed.MaxBytes);
        Assert.AreEqual(65536, parsed.Entry.MaxEntryBytes);
        Assert.IsTrue(parsed.Entry.ExposeHeaders);
        Assert.IsTrue(parsed.Entry.HeuristicFreshness);
        Assert.AreEqual(TimeSpan.FromSeconds(30), parsed.Entry.DefaultTtl);
        Assert.IsFalse(parsed.Entry.Collapse);
        Assert.AreEqual(8, parsed.Entry.CollapseMaxWaiters);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), parsed.Entry.CollapseTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(10), parsed.Entry.StaleRetention);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    [DataRow("cache.maxBytes", "lots")]
    [DataRow("cache.maxBytes", "-5")]
    [DataRow("cache.maxBytes", "10")]
    [DataRow("cache.maxEntryBytes", "0")]
    [DataRow("cache.exposeHeaders", "maybe")]
    [DataRow("cache.collapse.timeoutMs", "0")]
    public void InvalidValues_WarnAndFallBackToDefaults(string key, string value)
    {
        var warnings = new List<string>();
        var parsed = RunCommand.ParseResponseCacheOptions(Opts((key, value)), warnings.Add);

        Assert.AreEqual(1, warnings.Count, string.Join("|", warnings));
        StringAssert.Contains(warnings[0], key);
        Assert.AreEqual(64L * 1024 * 1024, parsed.MaxBytes);
        Assert.AreEqual(1024 * 1024, parsed.Entry.MaxEntryBytes);
        Assert.IsFalse(parsed.Entry.ExposeHeaders);
    }

    [TestMethod]
    public void EntryLimit_AboveTheTotalBudget_IsClamped()
    {
        var warnings = new List<string>();
        var parsed = RunCommand.ParseResponseCacheOptions(Opts(
            ("cache.maxBytes", "65536"), ("cache.maxEntryBytes", "1048576")), warnings.Add);

        Assert.AreEqual(1, warnings.Count);
        Assert.IsTrue(parsed.Entry.MaxEntryBytes <= parsed.MaxBytes);
    }
}
