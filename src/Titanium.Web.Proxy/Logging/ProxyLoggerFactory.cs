using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Titanium.Web.Proxy.Logging;

/// <summary>
///     A minimal, dependency-free composite <see cref="ILoggerFactory" /> built only on top of
///     <c>Microsoft.Extensions.Logging.Abstractions</c> (no dependency on the full
///     <c>Microsoft.Extensions.Logging</c> package is required). It fans a log record out to every
///     registered built-in provider and applies <see cref="ProxyLoggingOptions.MinimumLevel" /> once,
///     at the aggregate logger, so disabled levels never reach a provider.
/// </summary>
internal sealed class ProxyLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> providers = new();
    private readonly ConcurrentDictionary<string, ProxyLogger> loggers = new();
    private bool disposed;

    public ProxyLoggerFactory(LogLevel minimumLevel)
    {
        MinimumLevel = minimumLevel;
    }

    internal LogLevel MinimumLevel { get; }

    internal IReadOnlyList<ILoggerProvider> Providers => providers;

    public void AddProvider(ILoggerProvider provider)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ProxyLoggerFactory));
        providers.Add(provider);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return loggers.GetOrAdd(categoryName, name => new ProxyLogger(this, name));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (var provider in providers)
            try
            {
                provider.Dispose();
            }
            catch
            {
                // A misbehaving sink must never prevent the rest of the proxy from shutting down cleanly.
            }
    }
}

/// <summary>
///     The aggregate <see cref="ILogger" /> handed out by <see cref="ProxyLoggerFactory" />. Fans every
///     enabled log call out to each built-in provider's own per-category logger.
/// </summary>
internal sealed class ProxyLogger : ILogger
{
    private readonly ProxyLoggerFactory factory;
    private readonly ILogger[] innerLoggers;

    public ProxyLogger(ProxyLoggerFactory factory, string categoryName)
    {
        this.factory = factory;
        innerLoggers = new ILogger[factory.Providers.Count];
        for (var i = 0; i < innerLoggers.Length; i++)
            innerLoggers[i] = factory.Providers[i].CreateLogger(categoryName);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return NullLogger.Instance.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= factory.MinimumLevel && innerLoggers.Length > 0;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        foreach (var logger in innerLoggers)
            logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
