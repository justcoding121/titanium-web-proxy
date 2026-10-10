using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     Captures every log entry (level, formatted message, exception) so tests can assert that a failure path
///     stays at Debug/Trace and never surfaces as Warning/Error noise.
/// </summary>
public sealed class LevelCapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> entries = new();

    public (LogLevel Level, string Message, Exception? Exception)[] Entries => entries.ToArray();

    public int Count(LogLevel level) => entries.Count(e => e.Level == level);

    public string Describe(LogLevel atLeast) =>
        string.Join(Environment.NewLine, entries.Where(e => e.Level >= atLeast).Select(e => $"[{e.Level}] {e.Message}"));

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly LevelCapturingLoggerFactory owner;

        public CapturingLogger(LevelCapturingLoggerFactory owner) => this.owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
