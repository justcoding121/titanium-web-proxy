using System;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Examples.Basic.Helpers
{
    /// <summary>
    ///     One-line console logger for proxy diagnostics (message + exception type, no stack dump).
    /// </summary>
    internal sealed class CompactConsoleLoggerFactory : ILoggerFactory
    {
        private readonly CompactConsoleLoggerProvider provider;

        public CompactConsoleLoggerFactory(Action<LogLevel, string> write)
        {
            provider = new CompactConsoleLoggerProvider(write);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose() => provider.Dispose();
    }

    internal sealed class CompactConsoleLoggerProvider : ILoggerProvider
    {
        private readonly Action<LogLevel, string> write;

        public CompactConsoleLoggerProvider(Action<LogLevel, string> write)
        {
            this.write = write ?? throw new ArgumentNullException(nameof(write));
        }

        public ILogger CreateLogger(string categoryName) => new CompactConsoleLogger(write);

        public void Dispose()
        {
        }

        private sealed class CompactConsoleLogger : ILogger
        {
            private readonly Action<LogLevel, string> write;

            public CompactConsoleLogger(Action<LogLevel, string> write)
            {
                this.write = write;
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel) || formatter == null)
                    return;

                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception == null)
                    return;

                var prefix = logLevel switch
                {
                    LogLevel.Warning => "WARN",
                    LogLevel.Error => "ERR",
                    LogLevel.Critical => "CRIT",
                    _ => logLevel.ToString().ToUpperInvariant()
                };

                var line = exception == null
                    ? $"{prefix}  {message}"
                    : $"{prefix}  {message} ({exception.GetType().Name})";

                write(logLevel, line);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            public void Dispose()
            {
            }
        }
    }
}
