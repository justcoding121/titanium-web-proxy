using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace Titanium.TestKit;

/// <summary>Optional Redis for integration tests. Reads <c>TITANIUM_TEST_REDIS</c> (default <c>127.0.0.1:6379</c>).</summary>
public static class RedisTestHelper
{
    public const string Category = "Redis";

    public static string EndPoints { get; } =
        Environment.GetEnvironmentVariable("TITANIUM_TEST_REDIS")?.Trim() is { Length: > 0 } configured
            ? configured
            : "127.0.0.1:6379";

    /// <summary>Unique key prefix for one test run so parallel suites never collide.</summary>
    public static string NewKeyPrefix() => "twp:test:" + Guid.NewGuid().ToString("N") + ":";

    /// <summary>Connects with a short timeout. Returns null when Redis is down or unreachable.</summary>
    public static async Task<IConnectionMultiplexer?> TryConnectAsync()
    {
        try
        {
            var config = ConfigurationOptions.Parse(EndPoints);
            config.AbortOnConnectFail = true;
            config.ConnectTimeout = 500;
            config.SyncTimeout = 500;
            var mux = await ConnectionMultiplexer.ConnectAsync(config).ConfigureAwait(false);
            if (!mux.IsConnected)
            {
                mux.Dispose();
                return null;
            }

            return mux;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When <c>TITANIUM_REQUIRE_REDIS=1</c> (Linux Redis CI job): <see cref="Assert.Fail"/>.
    /// Otherwise (including the Windows main build, which has no Redis service container): inconclusive.
    /// </summary>
    public static void SkipOrFailIfUnavailable(bool requireRedis = false)
    {
        if (requireRedis || RequireRedis)
        {
            Assert.Fail($"Redis is required but was unreachable at {EndPoints}.");
        }

        Assert.Inconclusive($"Redis unavailable at {EndPoints}; set TITANIUM_TEST_REDIS or start Redis to run this test.");
    }

    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the dedicated Redis CI job set <c>TITANIUM_REQUIRE_REDIS=1</c>.</summary>
    public static bool RequireRedis =>
        string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REQUIRE_REDIS"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REQUIRE_REDIS"), "true", StringComparison.OrdinalIgnoreCase);
}
