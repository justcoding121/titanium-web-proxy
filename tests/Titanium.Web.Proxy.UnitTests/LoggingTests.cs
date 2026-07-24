using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Covers the unified logging infrastructure (<see cref="ProxyLoggingOptions" />,
///     <see cref="ProxyLoggerFactory" />, and the built-in console/rolling-file sinks): enable/disable,
///     minimum-level filtering, and rolling-file behavior.
/// </summary>
[TestClass]
public class LoggingTests
{
    [TestMethod]
    public void ProxyLoggingOptions_Defaults_Are_Sane()
    {
        var options = new ProxyLoggingOptions();

        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(LogLevel.Error, options.MinimumLevel);
        Assert.IsTrue(options.EnableConsole);
        Assert.IsFalse(options.EnableFile);
        Assert.IsNull(options.LoggerFactory);
        Assert.IsTrue(options.MaxFileSizeBytes > 0);
        Assert.IsTrue(options.MaxRolledFiles >= 0);
        Assert.IsTrue(options.QueueCapacity > 0);
    }

    [TestMethod]
    public void ApplyLoggingConfiguration_Disabled_Produces_A_Fully_Disabled_Logger()
    {
        using var proxy = new ProxyServer(false, false, false);

        proxy.Logging.Enabled = false;
        proxy.ApplyLoggingConfiguration();

        Assert.IsFalse(proxy.Logger.IsEnabled(LogLevel.Critical),
            "disabling logging must produce a logger that is not enabled for any level, including Critical");
    }

    [TestMethod]
    public void ApplyLoggingConfiguration_Enabled_Respects_MinimumLevel()
    {
        using var proxy = new ProxyServer(false, false, false);

        // At least one sink must be active for the aggregate logger to be enabled for any level at all
        // (see ProxyLogger.IsEnabled) - keep the built-in console sink on, but redirect stdout/stderr so
        // the test does not spam the console.
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        try
        {
            proxy.Logging.Enabled = true;
            proxy.Logging.EnableConsole = true;
            proxy.Logging.MinimumLevel = LogLevel.Warning;
            proxy.ApplyLoggingConfiguration();

            Assert.IsFalse(proxy.Logger.IsEnabled(LogLevel.Information),
                "levels below MinimumLevel must be disabled");
            Assert.IsFalse(proxy.Logger.IsEnabled(LogLevel.Debug));
            Assert.IsTrue(proxy.Logger.IsEnabled(LogLevel.Warning),
                "MinimumLevel itself must be enabled");
            Assert.IsTrue(proxy.Logger.IsEnabled(LogLevel.Error));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public void ProxyLoggerFactory_Filters_Below_MinimumLevel_Before_Reaching_Providers()
    {
        var capturing = new CapturingProvider();
        var factory = new ProxyLoggerFactory(LogLevel.Warning);
        factory.AddProvider(capturing);

        var logger = factory.CreateLogger("test");
        logger.LogInformation("info should be filtered out");
        logger.LogWarning("warning should pass through");
        logger.LogError("error should pass through");

        Assert.AreEqual(2, capturing.Entries.Count);
        CollectionAssert.DoesNotContain(capturing.Messages, "info should be filtered out");
        CollectionAssert.Contains(capturing.Messages, "warning should pass through");
        CollectionAssert.Contains(capturing.Messages, "error should pass through");

        factory.Dispose();
    }

    [TestMethod]
    public void ProxyLoggerFactory_Fans_Out_To_Every_Registered_Provider()
    {
        var first = new CapturingProvider();
        var second = new CapturingProvider();
        var factory = new ProxyLoggerFactory(LogLevel.Trace);
        factory.AddProvider(first);
        factory.AddProvider(second);

        factory.CreateLogger("test").LogError("boom");

        Assert.AreEqual(1, first.Entries.Count);
        Assert.AreEqual(1, second.Entries.Count);

        factory.Dispose();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public void RollingFileLoggerProvider_Rolls_When_MaxFileSizeBytes_Is_Exceeded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "titanium-log-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "proxy.log");

        try
        {
            var options = new ProxyLoggingOptions
            {
                FilePath = filePath,
                // Small enough that a handful of entries definitely rolls the file at least once.
                MaxFileSizeBytes = 1024,
                MaxRolledFiles = 2,
                QueueCapacity = 4096
            };

            using (var provider = new RollingFileLoggerProvider(options))
            {
                var logger = provider.CreateLogger("test");
                for (var i = 0; i < 200; i++)
                    logger.LogError("this is a reasonably long log line to help exceed the byte threshold {Index}", i);

                // The loop above may (deterministically, depending on exact byte counts) end precisely on a
                // roll boundary, in which case the active file would have just been moved to ".1" with
                // nothing yet written to a fresh one. One final marked entry guarantees a fresh active file
                // exists and contains it, regardless of where the loop above happened to land.
                logger.LogError("final-marker-entry");

                // ChannelLoggerProviderBase.Dispose() drains the queue (bounded wait) before returning.
            }

            Assert.IsTrue(File.Exists(filePath), "the active log file should exist after writing entries");
            StringAssert.Contains(File.ReadAllText(filePath), "final-marker-entry");
            Assert.IsTrue(File.Exists(filePath + ".1"),
                "at least one roll should have happened given the small MaxFileSizeBytes");

            // MaxRolledFiles = 2: never more than "<path>.1" and "<path>.2" should accumulate.
            Assert.IsFalse(File.Exists(filePath + ".3"),
                "no more than MaxRolledFiles rolled files should ever be retained");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // best-effort cleanup only
            }
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public void RollingFileLoggerProvider_Disabled_Rolling_Still_Writes_Without_Rolled_Files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "titanium-log-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "proxy.log");

        try
        {
            var options = new ProxyLoggingOptions
            {
                FilePath = filePath,
                MaxFileSizeBytes = 256,
                MaxRolledFiles = 0,
                QueueCapacity = 4096
            };

            using (var provider = new RollingFileLoggerProvider(options))
            {
                var logger = provider.CreateLogger("test");
                for (var i = 0; i < 100; i++)
                    logger.LogError("log line {Index}", i);
            }

            Assert.IsTrue(File.Exists(filePath));
            Assert.IsFalse(File.Exists(filePath + ".1"),
                "MaxRolledFiles = 0 means the active file is reset instead of rolled");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // best-effort cleanup only
            }
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public void ConsoleLoggerProvider_Writes_Warning_And_Above_To_Error_Stream()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);

        try
        {
            var options = new ProxyLoggingOptions { QueueCapacity = 64 };
            using var provider = new ConsoleLoggerProvider(options);
            var logger = provider.CreateLogger("test");

            logger.LogInformation("info goes to stdout");
            logger.LogError("error goes to stderr");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        StringAssert.Contains(outWriter.ToString(), "info goes to stdout");
        StringAssert.Contains(errorWriter.ToString(), "error goes to stderr");
        StringAssert.DoesNotMatch(outWriter.ToString(), new System.Text.RegularExpressions.Regex("error goes to stderr"));
    }

    [TestMethod]
    [DataRow(LogLevel.Trace)]
    [DataRow(LogLevel.Debug)]
    [DataRow(LogLevel.Warning)]
    [DataRow(LogLevel.Error)]
    [DataRow(LogLevel.Critical)]
    public void ConsoleLoggerProvider_AnsiColorFor_Returns_A_Code_For_Every_Level_Except_Information(LogLevel level)
    {
        var color = ConsoleLoggerProvider.AnsiColorFor(level);

        Assert.IsNotNull(color, $"{level} should have a distinct color so it stands out from Information");
        StringAssert.StartsWith(color, "\x1b[");
    }

    [TestMethod]
    public void ConsoleLoggerProvider_AnsiColorFor_Information_Is_Null_ie_Default_Terminal_Color()
    {
        Assert.IsNull(ConsoleLoggerProvider.AnsiColorFor(LogLevel.Information));
    }

    [TestMethod]
    public void ConsoleLoggerProvider_Colorize_Wraps_Line_With_Color_And_Reset_When_A_Color_Applies()
    {
        var colored = ConsoleLoggerProvider.Colorize(LogLevel.Error, "the line");

        StringAssert.StartsWith(colored, "\x1b[31m");
        StringAssert.EndsWith(colored, "\x1b[0m");
        StringAssert.Contains(colored, "the line");
    }

    [TestMethod]
    public void ConsoleLoggerProvider_Colorize_Leaves_Information_Line_Unchanged()
    {
        Assert.AreEqual("the line", ConsoleLoggerProvider.Colorize(LogLevel.Information, "the line"));
    }

    [TestMethod]
    public void ConsoleLoggerProvider_ShouldColorize_Respects_EnableConsoleColors_Switch()
    {
        Assert.IsFalse(ConsoleLoggerProvider.ShouldColorize(false, streamIsRedirected: false, noColorEnvValue: null));
        Assert.IsTrue(ConsoleLoggerProvider.ShouldColorize(true, streamIsRedirected: false, noColorEnvValue: null));
    }

    [TestMethod]
    public void ConsoleLoggerProvider_ShouldColorize_Never_Colors_A_Redirected_Stream()
    {
        Assert.IsFalse(ConsoleLoggerProvider.ShouldColorize(true, streamIsRedirected: true, noColorEnvValue: null));
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("true")]
    [DataRow("anything-non-empty")]
    public void ConsoleLoggerProvider_ShouldColorize_Respects_NO_COLOR_Convention(string noColorValue)
    {
        Assert.IsFalse(ConsoleLoggerProvider.ShouldColorize(true, streamIsRedirected: false, noColorValue));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void ConsoleLoggerProvider_ShouldColorize_Allows_Color_When_NO_COLOR_Is_Unset_Or_Empty(string? noColorValue)
    {
        Assert.IsTrue(ConsoleLoggerProvider.ShouldColorize(true, streamIsRedirected: false, noColorValue));
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<string> Messages = new();
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(this);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingProvider owner;

            public CapturingLogger(CapturingProvider owner)
            {
                this.owner = owner;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Messages.Add(message);
                owner.Entries.Add((logLevel, message));
            }
        }
    }
}
