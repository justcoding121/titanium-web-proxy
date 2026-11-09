using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Malformed and abandoned scrapes against the metrics endpoint, and console output that must never leak a resolved
///     secret (including partial-line writes that skip the newline path).
/// </summary>
[TestClass]
[DoNotParallelize]
public class MetricsEndpointAndConsoleBehaviorTests
{
    // Bug class: a scraper that connects and hangs up (load-balancer TCP health probe) kills the accept loop or the
    // serving task, so the next real scrape is refused.
    [TestMethod]
    [Timeout(30_000)]
    public async Task ClientThatClosesWithoutSendingAnything_DoesNotStopTheEndpoint()
    {
        using var exposition = new MetricsExposition();
        using var endpoint = new MetricsHttpEndpoint(exposition, IPAddress.Loopback, 0);
        endpoint.Start();

        for (var i = 0; i < 3; i++)
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        }

        StringAssert.StartsWith(await RawAsync(endpoint.Port, "GET /metrics HTTP/1.1\r\nHost: x\r\n\r\n"), "HTTP/1.1 200 OK");
    }

    // Bug class: a garbage request line crashes the parser (index out of range) instead of answering 400.
    [TestMethod]
    [Timeout(30_000)]
    public async Task RequestLineWithoutAPath_Gets400()
    {
        using var exposition = new MetricsExposition();
        using var endpoint = new MetricsHttpEndpoint(exposition, IPAddress.Loopback, 0);
        endpoint.Start();

        var response = await RawAsync(endpoint.Port, "GARBAGE\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 400 Bad Request");
        StringAssert.Contains(response, "bad request");
    }

    // Bug class: a query string on the scrape URL (a scrape-config `params:` block) makes /metrics a 404.
    [TestMethod]
    [Timeout(30_000)]
    public async Task MetricsPathWithAQueryString_IsStillServed()
    {
        using var exposition = new MetricsExposition();
        using var endpoint = new MetricsHttpEndpoint(exposition, IPAddress.Loopback, 0);
        endpoint.Start();

        var response = await RawAsync(endpoint.Port, "GET /metrics?module=proxy HTTP/1.1\r\nHost: x\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 200 OK");
    }

    // Bug class: disposing the endpoint with a client still connected hangs shutdown, or leaves the port bound so the
    // next process cannot reuse it.
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispose_WithAnIdleClientConnected_ReleasesThePort()
    {
        var exposition = new MetricsExposition();
        var endpoint = new MetricsHttpEndpoint(exposition, IPAddress.Loopback, 0);
        endpoint.Start();
        var port = endpoint.Port;
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);

        endpoint.Dispose();
        exposition.Dispose();

        using var rebind = new TcpListener(IPAddress.Loopback, port);
        rebind.Start();
        Assert.AreEqual(port, ((IPEndPoint)rebind.LocalEndpoint).Port);
    }

    // Bug class: partial-line console writes bypass secret masking, so a resolved secret in an exception message
    // reaches the terminal or container log when a caller uses Write / WriteErrorRaw instead of WriteLine.
    [TestMethod]
    [Timeout(30_000)]
    public async Task PartialLineWrites_MaskResolvedSecretsAndSkipTheNewline()
    {
        var secret = "s3cr3t-" + Guid.NewGuid().ToString("N");
        var stdoutBuffer = new StringWriter();
        var stderrBuffer = new StringWriter();
        var stdout = TextWriter.Synchronized(stdoutBuffer);
        var stderr = TextWriter.Synchronized(stderrBuffer);
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            SecretRedactor.Register(secret);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            AsyncConsole.Write("out-a:");
            AsyncConsole.Write($"token={secret}:");
            AsyncConsole.WriteLine("out-end");
            AsyncConsole.WriteErrorRaw($"err-a:{secret}");
            AsyncConsole.WriteErrorRaw(":err-b");
            await AsyncConsole.FlushAsync();

            Assert.IsTrue(
                stdoutBuffer.ToString().Contains("out-a:token=***:out-end", StringComparison.Ordinal),
                "Write must join without newlines and mask the secret: " + stdoutBuffer);
            Assert.IsFalse(stdoutBuffer.ToString().Contains(secret, StringComparison.Ordinal));
            Assert.IsTrue(
                stderrBuffer.ToString().Contains("err-a:***:err-b", StringComparison.Ordinal),
                "WriteErrorRaw must join without newlines and mask the secret: " + stderrBuffer);
            Assert.IsFalse(stderrBuffer.ToString().Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            SecretRedactor.Clear();
        }
    }

    private static async Task<string> RawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
