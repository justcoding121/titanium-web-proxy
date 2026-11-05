using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;

namespace Titanium.Cli.Config;

/// <summary>
/// Sends every call to the logger <see cref="ProxyServer.Logger"/> holds at that moment. <c>ProxyServer.Start</c> rebuilds its
/// logger from the logging settings, so a plugin that kept the logger it was given at load time would write to a logger that
/// was replaced (and disposed) before the first request.
/// </summary>
internal sealed class LateBoundProxyLogger : ILogger
{
    private readonly ProxyServer _proxy;

    internal LateBoundProxyLogger(ProxyServer proxy) => _proxy = proxy;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _proxy.Logger.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _proxy.Logger.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _proxy.Logger.Log(logLevel, eventId, state, exception, formatter);
}
