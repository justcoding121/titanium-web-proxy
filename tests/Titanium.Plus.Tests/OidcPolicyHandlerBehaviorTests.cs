using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

/// <summary>
///     Entry guards for OIDC policy registration and evaluation.
///     Bug classes: non-session objects are admitted, missing PolicyHandlers still registers silently.
/// </summary>
[TestClass]
public class OidcPolicyHandlerBehaviorTests
{
    [TestMethod]
    public async Task EvaluateAsync_NonSession_Throws()
    {
        var handler = new OidcPolicyHandler();
        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            handler.EvaluateAsync(new object(), new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch() },
                new RoutePolicyConfig { Name = "oidc-pol", Kind = "oidc" }).AsTask());
        StringAssert.Contains(ex.Message, "request session");
    }

    [TestMethod]
    public void TryStart_WithoutPolicyHandlers_WarnsAndReturnsNull()
    {
        var logger = new RecordingLogger();
        var started = OidcLogin.TryStart(new PlusActivationContext
        {
            ProxyServer = new object(),
            Logger = logger,
            PolicyHandlers = null,
        });
        Assert.IsNull(started);
        StringAssert.Contains(logger.Text, "no policy handler list");
    }

    [TestMethod]
    public void TryStart_RegistersHandler_OnHostList()
    {
        var handlers = new List<IRoutePolicyHandler>();
        var logger = new RecordingLogger();
        var started = OidcLogin.TryStart(new PlusActivationContext
        {
            ProxyServer = new object(),
            Logger = logger,
            PolicyHandlers = handlers,
        });
        Assert.IsNotNull(started);
        Assert.AreEqual(1, handlers.Count);
        Assert.AreSame(started, handlers[0]);
        Assert.AreEqual("oidc", started!.Kind);
        StringAssert.Contains(logger.Text, "login policy handler registered");
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
