using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal <see cref="ILoggerFactory" />/<see cref="ILogger" /> test double that captures every
///     exception reported through the proxy's centralized logging gateway (<c>ProxyDiagnostics</c>),
///     mirroring the removed <c>ProxyServer.ExceptionFunc</c> callback these tests used to assert
///     against. Always enabled for every level, so it observes both "benign" (Debug/Trace) and
///     "unexpected" (Error/Critical) reports, exactly like the old callback fired for every exception
///     regardless of severity.
/// </summary>
public sealed class TestExceptionCapture : ILoggerFactory
{
    private readonly ConcurrentQueue<Exception> exceptions = new();
    private volatile Exception? lastException;

    /// <summary>
    ///     The most recently reported exception, or <see langword="null" /> if none has been reported yet.
    ///     Matches the single-field "last write wins" pattern the old
    ///     <c>proxy.ExceptionFunc = ex => observedException = ex;</c> callback used.
    /// </summary>
    public Exception? LastException => lastException;

    /// <summary>
    ///     Every exception reported so far, in report order.
    /// </summary>
    public IReadOnlyCollection<Exception> Exceptions => exceptions;

    public void AddProvider(ILoggerProvider provider)
    {
        // No-op: this factory has no external providers to register.
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(this);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly TestExceptionCapture owner;

        public CapturingLogger(TestExceptionCapture owner)
        {
            this.owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception == null) return;

            owner.exceptions.Enqueue(exception);
            owner.lastException = exception;
        }
    }
}
