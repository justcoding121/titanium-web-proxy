using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Proxy OnBeforeRequest enumerates AutoResponder/Map Remote rules while the UI may
/// Add/Remove. Same InvalidOperationException pattern as the WebSocket frame list —
/// swallowed on the proxy thread today, but still a real race.
/// </summary>
[TestClass]
public class RuleListRaceTests
{
    [TestMethod]
    public void AutoResponder_TryMatch_SurvivesConcurrentAddRemove()
    {
        var vm = new AutoResponderViewModel { Enabled = true };
        vm.AddRule(new AutoResponderRule { MatchUrl = "*", Enabled = true, StatusCode = 204 });

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                vm.AddRule(new AutoResponderRule
                {
                    MatchUrl = "https://example/" + n++,
                    Enabled = true,
                    StatusCode = 200,
                });
                if (vm.Rules.Count > 8)
                {
                    vm.ClearRules();
                    vm.AddRule(new AutoResponderRule { MatchUrl = "*", Enabled = true, StatusCode = 204 });
                }
            }
        });

        Exception? caught = null;
        try
        {
            for (var i = 0; i < 20_000; i++)
            {
                try
                {
                    _ = vm.TryMatch("https://example.com/", null, out _);
                    _ = vm.HasEnabledGraphQlRule();
                }
                catch (Exception ex)
                {
                    caught = ex;
                    break;
                }
            }
        }
        finally
        {
            stop.Cancel();
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(5)), "writer did not stop");
        }

        Assert.IsNull(caught, $"AutoResponder rule matching must stay race-free; got {caught}");
    }

    [TestMethod]
    public void MapRemote_TryRewrite_SurvivesConcurrentAddRemove()
    {
        var vm = new MapRemoteViewModel { Enabled = true };
        vm.AddRule(new MapRemoteRule
        {
            MatchUrl = "https://a.test/*",
            TargetUrl = "https://b.test/$1",
            Enabled = true,
        });

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                vm.AddRule(new MapRemoteRule
                {
                    MatchUrl = "https://x" + n++ + ".test/*",
                    TargetUrl = "https://y.test/",
                    Enabled = true,
                });
                if (vm.Rules.Count > 8)
                {
                    vm.ClearRules();
                    vm.AddRule(new MapRemoteRule
                    {
                        MatchUrl = "https://a.test/*",
                        TargetUrl = "https://b.test/$1",
                        Enabled = true,
                    });
                }
            }
        });

        Exception? caught = null;
        try
        {
            for (var i = 0; i < 20_000; i++)
            {
                try
                {
                    _ = vm.TryRewrite("https://a.test/path", null, out _, out _);
                    _ = vm.HasEnabledGraphQlRule();
                }
                catch (Exception ex)
                {
                    caught = ex;
                    break;
                }
            }
        }
        finally
        {
            stop.Cancel();
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(5)), "writer did not stop");
        }

        Assert.IsNull(caught, $"Map Remote rule matching must stay race-free; got {caught}");
    }
}
