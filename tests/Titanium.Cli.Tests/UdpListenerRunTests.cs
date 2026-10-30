using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class UdpListenerRunTests
{
    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task EchoOnceAsync(Socket server, CancellationToken token)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await server.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
                await server.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, result.RemoteEndPoint, token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private static async Task<string?> RoundTripAsync(int port, string text)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        client.SendTo(Encoding.ASCII.GetBytes(text), new IPEndPoint(IPAddress.Loopback, port));
        var buffer = new byte[2048];
        try
        {
            var result = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [TestMethod]
    public async Task UdpOnlyConfig_RunsWithoutAnyHttpListener_ThroughForwardHostAndCluster()
    {
        using var echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var forwardPort = FreeUdpPort();
        var clusterPort = FreeUdpPort();

        var dir = Path.Combine(Path.GetTempPath(), "twp-udp-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {forwardPort}
                type: udp
                forwardHost: "127.0.0.1"
                forwardPort: {echoPort}
                idleTimeoutSeconds: 5
              - host: "127.0.0.1"
                port: {clusterPort}
                type: udp
                clusterId: echo
                maxSessions: 100
            clusters:
              - id: echo
                destinations:
                  - id: echo-1
                    address: "127.0.0.1"
                    port: {echoPort}
            """);

        using var stop = new CancellationTokenSource();
        using var echoStop = new CancellationTokenSource();
        var echoTask = EchoOnceAsync(echo, echoStop.Token);
        try
        {
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: true, stop.Token);

            string? viaForward = null;
            string? viaCluster = null;
            for (var attempt = 0; attempt < 20 && (viaForward is null || viaCluster is null); attempt++)
            {
                await Task.Delay(250);
                viaForward ??= await RoundTripAsync(forwardPort, "forward");
                viaCluster ??= await RoundTripAsync(clusterPort, "cluster");
            }

            Assert.AreEqual("forward", viaForward);
            Assert.AreEqual("cluster", viaCluster);

            await stop.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await stop.CancelAsync();
            await echoStop.CancelAsync();
            await echoTask;
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void BuildUdpOptions_MapsEveryListenerField()
    {
        var options = RunCommand.BuildUdpOptions(new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 5353,
            Type = "udp",
            ForwardHost = "10.0.0.5",
            ForwardPort = 53,
            IdleTimeoutSeconds = 12,
            MaxSessions = 77,
            MaxSessionsPerClient = 7,
            MaxDatagramBytes = 512,
            MaxDatagramsPerSecondPerClient = 99,
        });

        Assert.AreEqual(IPAddress.Loopback, options.BindAddress);
        Assert.AreEqual(5353, options.Port);
        Assert.AreEqual("10.0.0.5", options.ForwardHost);
        Assert.AreEqual(53, options.ForwardPort);
        Assert.AreEqual(TimeSpan.FromSeconds(12), options.IdleTimeout);
        Assert.AreEqual(77, options.MaxSessions);
        Assert.AreEqual(7, options.MaxSessionsPerClient);
        Assert.AreEqual(512, options.MaxDatagramBytes);
        Assert.AreEqual(99, options.MaxDatagramsPerSecondPerClient);
    }

    [TestMethod]
    public void BuildUdpOptions_UnsetFieldsKeepTheDocumentedDefaults()
    {
        var options = RunCommand.BuildUdpOptions(new ListenerConfig { Port = 5353, Type = "udp", ClusterId = "dns" });

        Assert.AreEqual(IPAddress.Any, options.BindAddress);
        Assert.AreEqual("dns", options.ClusterId);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.IdleTimeout);
        Assert.AreEqual(10_000, options.MaxSessions);
        Assert.AreEqual(1500, options.MaxDatagramBytes);
    }
}
