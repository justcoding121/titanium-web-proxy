using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.State;

/// <summary>
/// Redis-backed <see cref="IFleetRateStore"/> using one Lua script for an atomic token bucket.
/// Keys are <c>twp:rl2:</c> plus a hash of the logical key — no Redis hash-tag braces so Cluster shards normally.
/// </summary>
public sealed class RedisFleetRateStore : IFleetRateStore, IDisposable
{
    /// <summary>Live instances. Guardrail tests assert this stays 0 without <c>scope: fleet</c>.</summary>
    public static int LiveStores => Volatile.Read(ref _liveStores);
    private static int _liveStores;

    private const string LeaseLua = """
        local key = KEYS[1]
        local want = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local window = tonumber(ARGV[3])
        local burst = tonumber(ARGV[4])
        local token_bucket = tonumber(ARGV[5])
        local time = redis.call('TIME')
        local now = tonumber(time[1]) + tonumber(time[2]) / 1000000

        local tokens = tonumber(redis.call('HGET', key, 't'))
        local last = tonumber(redis.call('HGET', key, 'ts'))
        if tokens == nil then
          tokens = burst
          last = now
        end

        if token_bucket == 1 then
          local rate = limit / window
          local elapsed = now - last
          if elapsed > 0 then
            tokens = math.min(burst, tokens + elapsed * rate)
            last = now
          end
        else
          if (now - last) >= window then
            tokens = limit
            last = now
          end
        end

        local granted = math.min(want, math.floor(tokens))
        tokens = tokens - granted
        redis.call('HSET', key, 't', tokens, 'ts', last)
        redis.call('PEXPIRE', key, math.ceil(window * 1000) * 2)
        return granted
        """;

    private readonly IConnectionMultiplexer _mux;
    private bool _disposed;

    public RedisFleetRateStore(IConnectionMultiplexer mux)
    {
        _mux = mux;
        Interlocked.Increment(ref _liveStores);
    }

    /// <summary>
    /// Starts only when <c>state.redis</c> is set and a <c>rateLimit</c> policy has <c>scope: fleet</c>
    /// (or <c>state.fleetRate=true</c>). Default-off: no Redis connection for fleet otherwise.
    /// </summary>
    public static RedisFleetRateStore? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        var hasRedis = options.TryGetValue("state.redis", out var redis) && !string.IsNullOrWhiteSpace(redis);
        if (!hasRedis)
        {
            return null;
        }

        var explicitFleet = string.Equals(options.GetValueOrDefault("state.fleetRate"), "true", StringComparison.OrdinalIgnoreCase);
        if (!explicitFleet && !HasFleetRateLimit(context.Policies))
        {
            return null;
        }

        var where = "(unparsed)";
        try
        {
            var config = ConfigurationOptions.Parse(redis!);
            where = string.Join(",", config.EndPoints.Select(static e => e.ToString()));
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = 500;
            config.SyncTimeout = 500;
            var mux = ConnectionMultiplexer.Connect(config);
            if (!mux.IsConnected)
            {
                PlusLog.Warn(context, $"Plus FleetRate: redis={where} not connected — fleet leases will fail closed/local per policy.");
                mux.Dispose();
                return null;
            }

            var store = new RedisFleetRateStore(mux);
            context.FleetRateStore = store;
            PlusLog.Info(context, $"Plus FleetRate: redis={where}");
            return store;
        }
        catch (Exception ex)
        {
            PlusLog.Warn(context,
                $"Plus FleetRate: redis={where} unreachable ({ex.GetType().Name}) — fleet store not started.");
            return null;
        }
    }

    internal static bool HasFleetRateLimit(IReadOnlyList<RoutePolicyConfig>? policies)
    {
        if (policies is not { Count: > 0 })
        {
            return false;
        }

        for (var i = 0; i < policies.Count; i++)
        {
            var p = policies[i];
            if (string.Equals(p.Kind, "rateLimit", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Scope, "fleet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<FleetLeaseResult> LeaseAsync(
        string key,
        int want,
        FleetRateSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !_mux.IsConnected)
        {
            return new FleetLeaseResult { Granted = 0, StoreAvailable = false };
        }

        want = Math.Max(0, want);
        if (want == 0)
        {
            return new FleetLeaseResult { Granted = 0, StoreAvailable = true };
        }

        try
        {
            var db = _mux.GetDatabase();
            var redisKey = (RedisKey)BuildRedisKey(key);
            var task = db.ScriptEvaluateAsync(
                LeaseLua,
                [redisKey],
                [
                    want,
                    Math.Max(spec.Limit, 1),
                    Math.Max(spec.WindowSeconds, 1),
                    Math.Max(spec.Burst, 1),
                    spec.TokenBucket ? 1 : 0,
                ]);
            var granted = await task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new FleetLeaseResult
            {
                Granted = (int)(long)granted,
                StoreAvailable = true,
            };
        }
        catch (Exception)
        {
            return new FleetLeaseResult { Granted = 0, StoreAvailable = false };
        }
    }

    /// <summary>
    /// <c>twp:rl2:</c> + hash of the logical key. No curly-brace hash tags — Cluster distributes by full key.
    /// </summary>
    internal static string BuildRedisKey(string logicalKey)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(logicalKey), hash);
        return "twp:rl2:" + Convert.ToHexString(hash[..16]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Decrement(ref _liveStores);
        _mux.Dispose();
    }
}
