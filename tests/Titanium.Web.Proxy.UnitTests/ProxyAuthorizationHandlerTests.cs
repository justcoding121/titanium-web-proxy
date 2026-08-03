using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Exercises <see cref="ProxyServer" /> client authorization (407) for explicit proxy endpoints.
/// </summary>
[TestClass]
public class ProxyAuthorizationHandlerTests
{
    private static readonly string[] CustomOtherSchemes = { "Custom", "Other" };
    private static readonly string[] CustomOnlySchemes = { "Custom" };
    private static readonly string[] NegotiateNtlmSchemes = { "Negotiate", "NTLM" };

    [TestMethod]
    public async Task Missing_ProxyAuthorization_Returns_407_Required()
    {
        using var proxy = StartProxy(p => p.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(true));

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port);

        AssertStatusLine(response, 407, "Proxy Authentication Required");
        StringAssert.Contains(response, "Proxy-Authenticate: Basic realm=\"TitaniumProxy\"");
        StringAssert.Contains(response, "Connection: close");
    }

    [TestMethod]
    public async Task Malformed_ProxyAuthorization_NotExactlyOneSpace_Returns_407_Invalid()
    {
        using var proxy = StartProxy(p => p.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(true));

        var noSpace = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, "BasicNoSpace");
        AssertStatusLine(noSpace, 407, "Proxy Authentication Invalid");

        var twoSpaces = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, "Basic  a  b");
        AssertStatusLine(twoSpaces, 407, "Proxy Authentication Invalid");
    }

    [TestMethod]
    public async Task Basic_WrongScheme_Returns_407_Invalid()
    {
        using var proxy = StartProxy(p => p.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(true));

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, "Bearer token");

        AssertStatusLine(response, 407, "Proxy Authentication Invalid");
    }

    [TestMethod]
    public async Task Basic_MissingColonInDecodedCredentials_Returns_407_Invalid()
    {
        using var proxy = StartProxy(p => p.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(true));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("useronly"));

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, $"Basic {encoded}");

        AssertStatusLine(response, 407, "Proxy Authentication Invalid");
    }

    [TestMethod]
    public async Task Basic_RejectedByCallback_Returns_407_Invalid()
    {
        using var proxy = StartProxy(p =>
            p.ProxyBasicAuthenticateFunc = (_, user, pass) =>
                Task.FromResult(user == "alice" && pass == "secret"));

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong"));
        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, $"Basic {encoded}");

        AssertStatusLine(response, 407, "Proxy Authentication Invalid");
    }

    [TestMethod]
    public async Task Basic_Accepted_DoesNotReturn_407()
    {
        using var proxy = StartProxy(p =>
            p.ProxyBasicAuthenticateFunc = (_, user, pass) =>
                Task.FromResult(user == "alice" && pass == "secret"));

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, $"Basic {encoded}");

        Assert.IsFalse(response.StartsWith("HTTP/1.1 407", StringComparison.Ordinal), response);
    }

    [TestMethod]
    public async Task SchemeAuth_Success_Allows_Request()
    {
        using var proxy = StartProxy(p =>
        {
            p.ProxySchemeAuthenticateFunc = (_, scheme, creds) =>
                Task.FromResult(scheme == "Custom" && creds == "token"
                    ? ProxyAuthenticationContext.Succeeded()
                    : ProxyAuthenticationContext.Failed());
            p.ProxyAuthenticationSchemes = CustomOtherSchemes;
        });

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, "Custom token");

        Assert.IsFalse(response.StartsWith("HTTP/1.1 407", StringComparison.Ordinal), response);
    }

    [TestMethod]
    public async Task SchemeAuth_ContinuationNeeded_Returns_407_WithChallenge()
    {
        using var proxy = StartProxy(p =>
        {
            p.ProxySchemeAuthenticateFunc = (_, _, _) => Task.FromResult(new ProxyAuthenticationContext
            {
                Result = ProxyAuthenticationResult.ContinuationNeeded,
                Continuation = "Custom challenge=round2"
            });
            p.ProxyAuthenticationSchemes = CustomOnlySchemes;
        });

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, "Custom stale");

        AssertStatusLine(response, 407, "Proxy Authentication Invalid");
        StringAssert.Contains(response, "Proxy-Authenticate: Custom challenge=round2");
        StringAssert.Contains(response, "Connection: keep-alive");
    }

    [TestMethod]
    public async Task SchemeAuth_ConfiguredSchemes_AreAdvertised_On_407()
    {
        using var proxy = StartProxy(p =>
        {
            p.ProxySchemeAuthenticateFunc = (_, _, _) => Task.FromResult(ProxyAuthenticationContext.Failed());
            p.ProxyAuthenticationSchemes = NegotiateNtlmSchemes;
        });

        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port);

        AssertStatusLine(response, 407, "Proxy Authentication Required");
        StringAssert.Contains(response, "Proxy-Authenticate: Negotiate");
        StringAssert.Contains(response, "Proxy-Authenticate: NTLM");
        Assert.IsFalse(response.Contains("Basic realm=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Callback_Exception_Returns_407_AndReportsProxyAuthorizationException()
    {
        var capture = new ExceptionCapture();
        using var proxy = StartProxy(p =>
        {
            p.Logging.LoggerFactory = capture;
            p.ApplyLoggingConfiguration();
            p.ProxyBasicAuthenticateFunc = (_, _, _) => throw new InvalidOperationException("auth boom");
        });

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));
        var response = await SendConnectAsync(proxy.ProxyEndPoints[0].Port, $"Basic {encoded}");

        AssertStatusLine(response, 407, "Proxy Authentication Invalid");

        ProxyAuthorizationException? authEx = null;
        for (var i = 0; i < 50 && authEx == null; i++)
        {
            authEx = capture.Exceptions.OfType<ProxyAuthorizationException>().FirstOrDefault();
            if (authEx == null) await Task.Delay(20);
        }

        Assert.IsNotNull(authEx, "Expected ProxyAuthorizationException to be reported.");
        Assert.AreEqual("auth boom", authEx!.InnerException!.Message);
    }

    private static ProxyServer StartProxy(Action<ProxyServer>? configure = null)
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        configure?.Invoke(proxy);
        proxy.Start();
        return proxy;
    }

    private static async Task<string> SendConnectAsync(int port, string? proxyAuthValue = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var request = new StringBuilder();
        request.Append("CONNECT example.com:443 HTTP/1.1\r\n");
        request.Append("Host: example.com:443\r\n");
        if (proxyAuthValue != null)
            request.Append(KnownHeaders.ProxyAuthorization).Append(": ").Append(proxyAuthValue).Append("\r\n");
        request.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));

        var buffer = new byte[4096];
        var total = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static void AssertStatusLine(string response, int statusCode, string description)
    {
        var firstLine = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        Assert.AreEqual($"HTTP/1.1 {statusCode} {description}", firstLine);
    }

    private sealed class ExceptionCapture : ILoggerFactory
    {
        private readonly ConcurrentQueue<Exception> exceptions = new();
        public IReadOnlyCollection<Exception> Exceptions => exceptions;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(exceptions);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<Exception> exceptions;

            public CapturingLogger(ConcurrentQueue<Exception> exceptions)
            {
                this.exceptions = exceptions;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception != null) exceptions.Enqueue(exception);
            }
        }
    }
}
