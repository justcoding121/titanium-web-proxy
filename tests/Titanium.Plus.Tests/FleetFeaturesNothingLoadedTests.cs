using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

/// <summary>
/// Default-off guardrail: without <c>scope: fleet</c>, <c>plus.manager</c>, or dashboard SSO, Plus must not
/// open a fleet Redis store or start a manager agent loop.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FleetFeaturesNothingLoadedTests
{
    [TestMethod]
    public void DefaultPlusActivation_DoesNotStartFleetStoreOrManager()
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["controlPlane.host"] = "127.0.0.1",
            ["controlPlane.port"] = "0",
            ["controlPlane.sharedSecret"] = "test-secret-not-for-production-use-32b",
        };

        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Options = options,
            Middleware = new List<Titanium.Web.Proxy.Abstractions.Middleware.IProxyMiddleware>(),
            Policies =
            [
                new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 100, WindowSeconds = 60 },
            ],
            Routes = [],
        };

        // Fleet store type may be absent until the Redis seam lands; when present, LiveStores must stay 0.
        AssertFleetStoreIdle(context, options, expectStart: false);
        Assert.IsNull(context.FleetRateStore);

        var managerType = typeof(TitaniumPlusModule).Assembly.GetType("Titanium.Plus.Manager.ManagerAgent");
        if (managerType is not null)
        {
            var tryStart = managerType.GetMethod("TryStart", BindingFlags.Public | BindingFlags.Static);
            if (tryStart is not null)
            {
                Assert.IsNull(tryStart.Invoke(null, [context, options]));
            }

            var live = managerType.GetProperty("LiveLoops", BindingFlags.Public | BindingFlags.Static)
                       ?? managerType.GetProperty("LiveLoops", BindingFlags.NonPublic | BindingFlags.Static);
            if (live is not null)
            {
                Assert.AreEqual(0, (int)live.GetValue(null)!);
            }
        }
    }

    [TestMethod]
    public void FleetScopeWithoutRedis_DoesNotCreateFleetStore()
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["state.mode"] = "memory",
        };
        var context = new PlusActivationContext
        {
            ProxyServer = new object(),
            Options = options,
            Policies =
            [
                new RoutePolicyConfig
                {
                    Name = "rate",
                    Kind = "rateLimit",
                    Limit = 10,
                    Scope = "fleet",
                    FleetFailMode = "local",
                },
            ],
        };

        AssertFleetStoreIdle(context, options, expectStart: false);
        Assert.IsNull(context.FleetRateStore);
    }

    private static void AssertFleetStoreIdle(
        PlusActivationContext context,
        IReadOnlyDictionary<string, string> options,
        bool expectStart)
    {
        var storeType = typeof(TitaniumPlusModule).Assembly.GetType("Titanium.Plus.State.RedisFleetRateStore");
        if (storeType is null)
        {
            Assert.IsFalse(expectStart);
            return;
        }

        var tryStart = storeType.GetMethod("TryStart", BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(tryStart);
        var started = tryStart!.Invoke(null, [context, options]);
        if (expectStart)
        {
            Assert.IsNotNull(started);
        }
        else
        {
            Assert.IsNull(started);
        }

        var live = storeType.GetProperty("LiveStores", BindingFlags.Public | BindingFlags.Static);
        if (live is not null)
        {
            Assert.AreEqual(0, (int)live.GetValue(null)!);
        }
    }
}
