using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Titanium.Plus;
using Titanium.Plus.Security;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Plus.State;

/// <summary>Opt-in fixed-window rate limits (in-memory or Redis-backed).</summary>
public sealed class SharedStateStore
{
    public static SharedStateStore? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        var mode = options.GetValueOrDefault("state.mode")?.Trim().ToLowerInvariant();
        var hasRedis = options.TryGetValue("state.redis", out var redis) && !string.IsNullOrWhiteSpace(redis);
        var useMemory = string.Equals(mode, "memory", StringComparison.Ordinal);
        if (!useMemory && !hasRedis)
        {
            return null;
        }

        var limit = int.TryParse(options.GetValueOrDefault("state.rateLimitPerMinute"), out var n) && n > 0
            ? n
            : 120;

        IDistributedCounter counter;
        if (useMemory && !hasRedis)
        {
            counter = new InMemoryDistributedCounter();
            PlusLog.Info(context, $"Plus State: mode=memory rateLimitPerMinute={limit}");
        }
        else
        {
            try
            {
                var config = ConfigurationOptions.Parse(redis!);
                config.AbortOnConnectFail = false;
                config.ConnectTimeout = 500;
                config.SyncTimeout = 500;
                var mux = ConnectionMultiplexer.Connect(config);
                if (!mux.IsConnected)
                {
                    PlusLog.Warn(context,
                        $"Plus State: redis={redis} not connected — rate limit fail-open (allow).");
                    return new SharedStateStore();
                }

                counter = new RedisDistributedCounter(mux);
                PlusLog.Info(context, $"Plus State: redis={redis} rateLimitPerMinute={limit}");
            }
            catch (Exception ex)
            {
                PlusLog.Warn(context,
                    $"Plus State: redis={redis} unreachable ({ex.Message}) — rate limit fail-open (allow).");
                return new SharedStateStore();
            }
        }

        if (context.Middleware is null)
        {
            PlusLog.Warn(context, "Plus State: Middleware list is null — rate limit not registered.");
            return new SharedStateStore();
        }

        context.Middleware.Add(new RateLimitMiddleware(counter, limit, context.Logger));
        return new SharedStateStore();
    }
}

/// <summary>Incrementing counter used by rate limiting (Redis or in-memory).</summary>
public interface IDistributedCounter
{
    Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>In-memory fixed-window counter for unit tests.</summary>
public sealed class InMemoryDistributedCounter : IDistributedCounter
{
    private readonly ConcurrentDictionary<string, (long Count, DateTimeOffset WindowStart)> _map = new();

    public Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _map.AddOrUpdate(
            key,
            _ => (1, now),
            (_, existing) =>
            {
                if (now - existing.WindowStart >= window)
                {
                    return (1, now);
                }

                return (existing.Count + 1, existing.WindowStart);
            });
        return Task.FromResult(entry.Count);
    }
}

internal sealed class RedisDistributedCounter : IDistributedCounter
{
    private readonly IConnectionMultiplexer _mux;

    public RedisDistributedCounter(IConnectionMultiplexer mux) => _mux = mux;

    public async Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var db = _mux.GetDatabase();
        var redisKey = $"twp:rl:{key}";
        var count = await db.StringIncrementAsync(redisKey).ConfigureAwait(false);
        if (count == 1)
        {
            await db.KeyExpireAsync(redisKey, window).ConfigureAwait(false);
        }

        return count;
    }
}

/// <summary>Fixed-window per-IP rate limit middleware.</summary>
public sealed class RateLimitMiddleware : IProxyMiddleware
{
    private readonly IDistributedCounter _counter;
    private readonly int _limitPerMinute;
    private readonly ILogger? _logger;
    private readonly Func<object, string>? _keyResolver;

    public RateLimitMiddleware(
        IDistributedCounter counter,
        int limitPerMinute,
        ILogger? logger = null,
        Func<object, string>? keyResolver = null)
    {
        _counter = counter;
        _limitPerMinute = limitPerMinute;
        _logger = logger;
        _keyResolver = keyResolver;
    }

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var key = ResolveKey(context.Session);
        try
        {
            var count = await _counter.IncrementAsync(key, TimeSpan.FromMinutes(1), cancellationToken)
                .ConfigureAwait(false);
            if (count > _limitPerMinute)
            {
                if (_logger?.IsEnabled(LogLevel.Information) == true)
                {
                    _logger.LogInformation("Plus State: rate limit exceeded for {Key}", key);
                }

                CidrAccessMiddleware.Deny(context, HttpStatusCode.TooManyRequests, "rate limit exceeded");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Plus State: rate limit counter failed — fail-open");
        }

        await next(context, cancellationToken);
    }

    private string ResolveKey(object session)
    {
        if (_keyResolver is not null)
        {
            return _keyResolver(session);
        }

        if (session is SessionEventArgsBase args)
        {
            return args.ClientRemoteEndPoint.Address.ToString();
        }

        return "unknown";
    }
}
