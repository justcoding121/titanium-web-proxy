using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
/// Terminate-lite is a cliff: one scripted, extAuth, oidc, gRPC-Web or connection-limited route moves the listener to the
/// full session path. The proxy must say which feature did it, once, and report the state on <c>twp.lite.eligible</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TerminateLiteEligibilityTests
{
    private const string LiteWarning = "H1 terminate-lite is disabled";

    private static RouteConfig Route(Action<RouteConfigBuilder>? configure = null)
    {
        var builder = new RouteConfigBuilder();
        configure?.Invoke(builder);
        return new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Policies = builder.Policies,
            Middleware = builder.Middleware,
            GrpcWeb = builder.GrpcWeb,
        };
    }

    private sealed class RouteConfigBuilder
    {
        public string[]? Policies { get; set; }
        public string[]? Middleware { get; set; }
        public bool GrpcWeb { get; set; }
    }

    private static ReverseProxyOptions Options(RouteConfig route, params RoutePolicyConfig[] policies) =>
        new() { Routes = [route], Policies = policies };

    private static string Joined(ProxyServer proxy) => string.Join(" | ", proxy.LiteBlockers());

    [TestMethod]
    public void NoFeature_LeavesTheListenerEligible()
    {
        using var proxy = new ProxyServer(false, false, false) { ReverseProxy = Options(Route()) };
        Assert.AreEqual(0, proxy.LiteBlockers().Count);

        using var bare = new ProxyServer(false, false, false);
        Assert.AreEqual(0, bare.LiteBlockers().Count);
    }

    [TestMethod]
    public void EachFeature_IsNamedByItsOwnReason()
    {
        using var plugins = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(Route(b => b.Middleware = ["script"])),
        };
        StringAssert.Contains(Joined(plugins), "per-route plugin");

        using var extAuth = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(
                Route(b => b.Policies = ["auth"]),
                new RoutePolicyConfig { Name = "auth", Kind = "extAuth", Url = "http://127.0.0.1:1/check" }),
        };
        StringAssert.Contains(Joined(extAuth), "extAuth or oidc");

        using var grpc = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(Route(b => b.GrpcWeb = true)),
        };
        StringAssert.Contains(Joined(grpc), "grpcWeb");

        using var connections = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(
                Route(b => b.Policies = ["slots"]),
                new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 4 }),
        };
        StringAssert.Contains(Joined(connections), "connectionLimit");

        using var compression = new ProxyServer(false, false, false)
        {
            ResponseCompression = new ResponseCompressionConfig { Enabled = true },
        };
        StringAssert.Contains(Joined(compression), "response compression");

        // A plain rate limit is decided without a session, so it keeps terminate-lite.
        using var rate = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(
                Route(b => b.Policies = ["rate"]),
                new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 5 }),
        };
        Assert.AreEqual(0, rate.LiteBlockers().Count);

        // Fleet rateLimit stays on terminate-lite (sync Check; refill is background-only).
        using var fleet = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(
                Route(b => b.Policies = ["fleet"]),
                new RoutePolicyConfig
                {
                    Name = "fleet",
                    Kind = "rateLimit",
                    Limit = 5,
                    Scope = "fleet",
                    FleetFailMode = "local",
                }),
        };
        Assert.AreEqual(0, fleet.LiteBlockers().Count);
    }

    [TestMethod]
    public void SeveralFeatures_AreAllNamed()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(Route(b => { b.GrpcWeb = true; b.Middleware = ["script"]; })),
        };
        var text = Joined(proxy);
        StringAssert.Contains(text, "per-route plugin");
        StringAssert.Contains(text, "grpcWeb");
    }

    [TestMethod]
    public async Task Start_WarnsOnceNamingTheFeature_AndTheGaugeFollowsTheConfiguration()
    {
        var logs = new CapturingFactory();
        using var proxy = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(Route(b => b.Middleware = ["script"])),
        };
        proxy.Logging.LoggerFactory = logs;
        proxy.ApplyLoggingConfiguration();
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 });

        var (zerosBefore, onesBefore) = Gauge();
        proxy.Start(changeSystemProxySettings: false);
        try
        {
            var warnings = logs.Warnings.Where(m => m.Contains(LiteWarning, StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(1, warnings.Length, "one warning at startup");
            StringAssert.Contains(warnings[0], "per-route plugin");
            StringAssert.Contains(warnings[0], "separate listener");

            Assert.AreEqual(0, proxy.LiteEligibleValue());
            var (zeros, ones) = Gauge();
            Assert.AreEqual(zerosBefore + 1, zeros, "twp.lite.eligible reports 0 for this proxy");
            Assert.AreEqual(onesBefore, ones);

            // A reload that drops the feature is visible on the next collection, with no second warning.
            proxy.ReverseProxy = Options(Route());
            Assert.AreEqual(1, proxy.LiteEligibleValue());
            var (zerosAfter, onesAfter) = Gauge();
            Assert.AreEqual(zerosBefore, zerosAfter);
            Assert.AreEqual(onesBefore + 1, onesAfter);
            Assert.AreEqual(1, logs.Warnings.Count(m => m.Contains(LiteWarning, StringComparison.Ordinal)));
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    [TestMethod]
    public async Task NoFixedForwardHostListener_NeitherWarnsNorReports()
    {
        var logs = new CapturingFactory();
        using var proxy = new ProxyServer(false, false, false)
        {
            ReverseProxy = Options(Route(b => b.Middleware = ["script"])),
        };
        proxy.Logging.LoggerFactory = logs;
        proxy.ApplyLoggingConfiguration();
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));

        proxy.Start(changeSystemProxySettings: false);
        try
        {
            Assert.IsNull(proxy.LiteEligibleValue(), "terminate-lite does not apply to an explicit listener");
            Assert.AreEqual(0, logs.Warnings.Count(m => m.Contains(LiteWarning, StringComparison.Ordinal)));
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    private static (int Zeros, int Ones) Gauge()
    {
        var values = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy" && instrument.Name == "twp.lite.eligible")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => values.Add(measurement));
        listener.Start();
        listener.RecordObservableInstruments();
        return (values.Count(v => v == 0), values.Count(v => v == 1));
    }

    private sealed class CapturingFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public string[] Warnings => entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
