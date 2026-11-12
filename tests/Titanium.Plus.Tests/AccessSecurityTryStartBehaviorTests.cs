using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

/// <summary>
///     AccessSecurity.TryStart registration edges (CIDR/JWT-only, null middleware).
/// </summary>
[TestClass]
public class AccessSecurityTryStartBehaviorTests
{
    [TestMethod]
    public void TryStart_NullMiddleware_WarnsAndReturnsInstance()
    {
        var logger = new RecordingLogger();
        var security = AccessSecurity.TryStart(
            new PlusActivationContext
            {
                ProxyServer = new object(),
                Logger = logger,
                Middleware = null,
                Options = new Dictionary<string, string> { ["security.allowCidrs"] = "10.0.0.0/8" },
            },
            new Dictionary<string, string> { ["security.allowCidrs"] = "10.0.0.0/8" });
        Assert.IsNotNull(security);
        StringAssert.Contains(logger.Text, "Middleware list is null");
    }

    [TestMethod]
    public void TryStart_CidrOnly_AndJwtOnly_RegisterMiddleware()
    {
        var cidrMw = new List<IProxyMiddleware>();
        Assert.IsNotNull(AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = cidrMw },
            new Dictionary<string, string> { ["security.allowCidrs"] = "127.0.0.0/8" }));
        Assert.AreEqual(1, cidrMw.Count);

        var jwtMw = new List<IProxyMiddleware>();
        Assert.IsNotNull(AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = jwtMw },
            new Dictionary<string, string>
            {
                ["security.jwtAuthority"] = "https://issuer.example",
                ["security.jwtAudience"] = "api",
                ["security.jwksUrl"] = "https://issuer.example/jwks",
            }));
        Assert.AreEqual(1, jwtMw.Count);

        Assert.IsNull(AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = [] },
            new Dictionary<string, string>()));
    }

    [TestMethod]
    public void TryStart_ApiKeysAndBasicUsers_RegisterAuthMiddleware()
    {
        var mw = new List<IProxyMiddleware>();
        var logger = new RecordingLogger();
        Assert.IsNotNull(AccessSecurity.TryStart(
            new PlusActivationContext { ProxyServer = new object(), Middleware = mw, Logger = logger },
            new Dictionary<string, string>
            {
                ["security.apiKeys"] = "k1, k2",
                ["security.basicUsers"] = "alice:secret,badentry,bob:other",
            }));
        Assert.AreEqual(1, mw.Count);
        StringAssert.Contains(logger.Text, "plaintext passwords");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly System.Text.StringBuilder _text = new();
        public string Text { get { lock (_text) { return _text.ToString(); } } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_text) { _text.AppendLine(formatter(state, exception)); }
        }
    }
}
